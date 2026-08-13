// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Coordinates one codeplug web-stream source, playback session, and monitor
    /// output. The injected source and audio factories are borrowed shared owners;
    /// this coordinator stops its per-run source/output but does not dispose either
    /// factory. The coordinator is headless: shell code subscribes to
    /// <see cref="StateChanged"/> and owns UI-thread dispatch and persistence.
    /// </summary>
    /// <remarks>
    /// Source PCM callbacks are consumed synchronously and copied before this
    /// coordinator returns from the callback. State notifications may originate
    /// from the decoder worker; subscribers must not retain callback memory. A
    /// subscriber may request stop or disposal from a state notification; the
    /// coordinator cancels without self-joining and completes teardown after the
    /// notification returns.
    /// </remarks>
    public sealed class WebStreamPlaybackCoordinator : IAsyncDisposable
    {
        private readonly Codeplug.WebStream _definition;
        private readonly IWebStreamSourceFactory _sourceFactory;
        private readonly IAudioStreamFactory _audioFactory;
        private readonly AudioDeviceId _outputDeviceId;
        private readonly Func<TimeSpan, Action, IDisposable> _scheduler;
        private readonly Func<DateTime> _utcNow;
        private readonly object _stateGate = new();

        private RunState? _run;
        private Task? _runTask;
        private CancellationTokenSource? _runCancellation;
        private bool _started;
        private bool _stopRequested;
        private bool _disposed;
        private readonly AsyncLocal<int> _notificationDepth = new();
        private string _statusText = "Off";
        private bool _isActive;
        private bool _isReceiving;
        private double _volume = 1.0;
        private IDisposable? _activityTimer;

        private static readonly TimeSpan ActivityTimerPeriod = TimeSpan.FromMilliseconds(250);

        public WebStreamPlaybackCoordinator(
            Codeplug.WebStream definition,
            IWebStreamSourceFactory sourceFactory,
            IAudioStreamFactory audioFactory,
            AudioDeviceId outputDeviceId,
            Func<TimeSpan, Action, IDisposable>? scheduler = null,
            Func<DateTime>? utcNow = null)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
            _audioFactory = audioFactory ?? throw new ArgumentNullException(nameof(audioFactory));
            _outputDeviceId = outputDeviceId;
            _scheduler = scheduler ?? DefaultSchedule;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public string DisplayName => _definition.Name ?? string.Empty;

        public string StatusText
        {
            get
            {
                lock (_stateGate)
                    return _statusText;
            }
        }

        public bool IsActive
        {
            get
            {
                lock (_stateGate)
                    return _isActive;
            }
        }

        public bool IsReceiving
        {
            get
            {
                lock (_stateGate)
                    return _isReceiving;
            }
        }

        public double Volume
        {
            get
            {
                lock (_stateGate)
                    return _volume;
            }
            set
            {
                var normalized = NormalizeVolume(value);
                RunState? run;
                lock (_stateGate)
                {
                    _volume = normalized;
                    run = _run;
                }

                if (run is not null)
                {
                    lock (run.SessionGate)
                    {
                        run.Session.Volume = normalized;
                    }

                    run.Pipeline.Volume = (float)normalized;
                }

                PublishState(run);
            }
        }

        public event Action<WebStreamPlaybackState>? StateChanged;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (_started)
                    throw new InvalidOperationException("The web-stream playback coordinator can only be started once.");

                _started = true;
                _stopRequested = false;
                _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _runTask = RunAsync(_runCancellation);
                return _runTask;
            }
        }

        public Task StopAsync()
        {
            RunState? run;
            Task? task;
            bool reentrant;
            lock (_stateGate)
            {
                if (_disposed)
                    return Task.CompletedTask;

                _stopRequested = true;
                _runCancellation?.Cancel();
                run = _run;
                task = _runTask;
                reentrant = IsInsideCallbackOrNotification();
            }

            CancelActivityTimer();

            if (run is not null)
            {
                lock (run.SessionGate)
                {
                    run.Session.Stop();
                }

                if (!reentrant)
                    PublishState(run);
            }

            if (run?.Source is not null)
            {
                if (reentrant)
                {
                    _ = StopSourceOnceAsync(run);
                    return Task.CompletedTask;
                }

                return StopAndJoinAsync(run, task);
            }

            return task ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            RunState? run;
            Task? task;
            bool reentrant;
            lock (_stateGate)
            {
                if (_disposed)
                    return ValueTask.CompletedTask;

                _disposed = true;
                _stopRequested = true;
                _runCancellation?.Cancel();
                run = _run;
                task = _runTask;
                reentrant = IsInsideCallbackOrNotification();
            }

            CancelActivityTimer();

            if (reentrant)
            {
                _ = DisposeAfterRunAsync(run, task);
                return ValueTask.CompletedTask;
            }

            return new ValueTask(DisposeAfterRunAsync(run, task));
        }

        private async Task RunAsync(CancellationTokenSource runCancellation)
        {
            RunState? run = null;
            IWebStreamSource? createdSource = null;
            try
            {
                var options = new WebStreamSourceOptions(
                    _definition.Url ?? string.Empty,
                    _definition.AuthUsername,
                    _definition.AuthPassword);
                var source = _sourceFactory.Create(options);
                createdSource = source;
                var pipeline = new MonitorAudioPipeline(_audioFactory);
                pipeline.Start(_outputDeviceId);
                run = new RunState(source, pipeline);

                var session = new WebStreamPlaybackSession((_, pcm, volume) =>
                {
                    pipeline.Volume = (float)volume;
                    var result = pipeline.WritePcm(pcm);
                    if (result.Status != AudioWriteStatus.Accepted)
                        Volatile.Write(ref run.OutputFailed, 1);
                }, _utcNow);
                run.Session = session;

                lock (_stateGate)
                {
                    _run = run;
                }

                session.Volume = Volume;
                session.Start();
                PublishState(run);

                var sourceTask = source.StartAsync(
                    pcm => OnPcm(run, pcm),
                    runCancellation.Token,
                    progress => OnProgress(run, progress));

                WebStreamSourceResult result;
                try
                {
                    result = await sourceTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
                {
                    result = null!;
                }

                if (result is not null && result.StopReason == WebStreamSourceStopReason.Failed)
                {
                    Volatile.Write(ref run.TerminalFailure, 1);
                    lock (run.SessionGate)
                    {
                        run.Session.MarkFailed();
                    }

                    PublishState(run);
                }
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
            {
            }
            catch
            {
                if (run is not null)
                {
                    Volatile.Write(ref run.TerminalFailure, 1);
                    lock (run.SessionGate)
                    {
                        run.Session.MarkFailed();
                    }

                    PublishState(run);
                }
                else
                {
                    await DisposeCreatedSourceAsync(createdSource).ConfigureAwait(false);
                    PublishTerminalFailure();
                }
            }
            finally
            {
                CancelActivityTimer();
                if (run is not null)
                {
                    lock (run.SessionGate)
                    {
                        if (IsStopRequested())
                            run.Session.Stop();
                    }

                    await DisposeSourceOnceAsync(run).ConfigureAwait(false);
                    await StopPipelineOnceAsync(run).ConfigureAwait(false);

                    if (IsStopRequested())
                        PublishState(run);
                }

                lock (_stateGate)
                {
                    if (ReferenceEquals(_runCancellation, runCancellation))
                    {
                        _runTask = null;
                        _runCancellation = null;
                    }
                }

                runCancellation.Dispose();
            }
        }

        private void OnPcm(RunState run, ReadOnlyMemory<byte> pcm)
        {
            _notificationDepth.Value++;
            try
            {
                if (pcm.IsEmpty)
                    return;

                var copy = pcm.ToArray();
                lock (run.SessionGate)
                {
                    run.Session.AppendPcm(copy);
                }

                if (Volatile.Read(ref run.OutputFailed) != 0)
                {
                    lock (run.SessionGate)
                    {
                        run.Session.MarkFailed();
                    }
                }

                PublishState(run);
                ScheduleActivityTimerIfNeeded(run);
            }
            finally
            {
                _notificationDepth.Value--;
            }
        }

        private void OnProgress(RunState run, WebStreamSourceProgress progress)
        {
            lock (run.SessionGate)
            {
                switch (progress.Kind)
                {
                    case WebStreamSourceProgressKind.Connecting:
                        run.Session.Start();
                        break;
                    case WebStreamSourceProgressKind.Retry:
                        run.Session.MarkRetry(progress.Attempt);
                        break;
                    case WebStreamSourceProgressKind.Connected:
                        run.Session.MarkConnected();
                        break;
                }
            }

            PublishState(run);
        }

        private async Task StopAndJoinAsync(RunState run, Task? task)
        {
            await StopSourceOnceAsync(run).ConfigureAwait(false);
            if (task is not null)
                await task.ConfigureAwait(false);
        }

        private static async Task StopSourceOnceAsync(RunState run)
        {
            if (Interlocked.Exchange(ref run.SourceStopped, 1) != 0)
                return;

            try
            {
                await run.Source.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task DisposeAfterRunAsync(RunState? run, Task? task)
        {
            if (run is not null)
                await StopSourceOnceAsync(run).ConfigureAwait(false);
            if (task is not null && !IsInsideCallbackOrNotification())
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            // The injected factories are borrowed owners. They may be shared
            // by several stream coordinators and are disposed by their shell
            // owner after all per-stream outputs and sources have stopped.
        }

        private static async Task DisposeSourceOnceAsync(RunState run)
        {
            if (Interlocked.Exchange(ref run.SourceDisposed, 1) != 0)
                return;

            try
            {
                await run.Source.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static async Task DisposeCreatedSourceAsync(IWebStreamSource? source)
        {
            if (source is null)
                return;

            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static async Task StopPipelineOnceAsync(RunState run)
        {
            if (Interlocked.Exchange(ref run.PipelineStopped, 1) != 0)
                return;

            try
            {
                await run.Pipeline.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private void ScheduleActivityTimerIfNeeded(RunState run)
        {
            lock (run.SessionGate)
            {
                if (!run.Session.IsReceiving)
                    return;
            }

            lock (_stateGate)
            {
                if (_disposed || _stopRequested || _activityTimer is not null)
                    return;
            }

            var handle = _scheduler(ActivityTimerPeriod, () => OnActivityTimer(run));
            lock (_stateGate)
            {
                if (_disposed || _stopRequested || !ReferenceEquals(_run, run))
                {
                    handle.Dispose();
                    return;
                }

                _activityTimer = handle;
            }
        }

        private void OnActivityTimer(RunState run)
        {
            lock (_stateGate)
            {
                if (_disposed || _stopRequested || !ReferenceEquals(_run, run))
                    return;

                _activityTimer = null;
            }

            lock (run.SessionGate)
                run.Session.Tick();

            PublishState(run);
            ScheduleActivityTimerIfNeeded(run);
        }

        private void CancelActivityTimer()
        {
            IDisposable? timer;
            lock (_stateGate)
            {
                timer = _activityTimer;
                _activityTimer = null;
            }

            timer?.Dispose();
        }

        private void PublishState(RunState? run)
        {
            if (run is null)
                return;

            string statusText;
            bool isActive;
            bool isReceiving;
            double volume;
            lock (_stateGate)
            {
                volume = _volume;
                isActive = Volatile.Read(ref run.TerminalFailure) == 0;
            }

            lock (run.SessionGate)
            {
                var session = run.Session;
                statusText = Volatile.Read(ref run.TerminalFailure) != 0
                    ? "Down"
                    : session.StatusText;
                isActive = isActive && session.StreamId != 0;
                isReceiving = session.IsReceiving;
            }

            WebStreamPlaybackState state;
            lock (_stateGate)
            {
                _statusText = statusText;
                _isActive = isActive;
                _isReceiving = isReceiving;
                state = new WebStreamPlaybackState(
                    statusText,
                    isActive,
                    isReceiving,
                    volume);
            }

            NotifyStateChanged(state);
        }

        private bool IsStopRequested()
        {
            lock (_stateGate)
                return _stopRequested;
        }

        private bool IsInsideCallbackOrNotification()
            => _notificationDepth.Value > 0;

        private void PublishTerminalFailure()
        {
            WebStreamPlaybackState state;
            lock (_stateGate)
            {
                _statusText = "Down";
                _isActive = false;
                _isReceiving = false;
                state = new WebStreamPlaybackState("Down", false, false, _volume);
            }

            NotifyStateChanged(state);
        }

        private void NotifyStateChanged(WebStreamPlaybackState state)
        {
            _notificationDepth.Value++;
            try
            {
                foreach (var subscriber in StateChanged?.GetInvocationList()
                    ?? Array.Empty<Delegate>())
                {
                    try
                    {
                        ((Action<WebStreamPlaybackState>)subscriber)(state);
                    }
                    catch
                    {
                        // A state observer must not fault the decoder worker.
                    }
                }
            }
            finally
            {
                _notificationDepth.Value--;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WebStreamPlaybackCoordinator));
        }

        private static double NormalizeVolume(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 1.0;

            return Math.Clamp(Math.Round(value * 10.0) / 10.0, 0.0, 4.0);
        }

        private sealed class TimerCancellation : IDisposable
        {
            private readonly Timer _timer;

            public TimerCancellation(Timer timer)
            {
                _timer = timer;
            }

            public void Dispose() => _timer.Dispose();
        }

        private static IDisposable DefaultSchedule(TimeSpan delay, Action action)
            => new TimerCancellation(new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan));

        private sealed class RunState
        {
            public RunState(IWebStreamSource source, MonitorAudioPipeline pipeline)
            {
                Source = source;
                Pipeline = pipeline;
            }

            public IWebStreamSource Source { get; }
            public MonitorAudioPipeline Pipeline { get; }
            public WebStreamPlaybackSession Session { get; set; } = null!;
            public object SessionGate { get; } = new();
            public int OutputFailed;
            public int SourceStopped;
            public int SourceDisposed;
            public int PipelineStopped;
            public int TerminalFailure;
        }
    }

    public sealed record WebStreamPlaybackState(
        string StatusText,
        bool IsActive,
        bool IsReceiving,
        double Volume);
}
