#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class WebStreamPlaybackCoordinatorTests
    {
        [Fact]
        public async Task StartAsync_ComposesSourceSessionAndOutput_AndCopiesPcmSynchronously()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = new WebStreamPlaybackCoordinator(
                new Codeplug.WebStream
                {
                    Name = "Dispatch feed",
                    Url = "https://radio.example.test/feed.mp3",
                    AuthUsername = "listener",
                    AuthPassword = "secret",
                },
                sourceFactory,
                audioFactory,
                AudioDeviceId.Default);
            var states = new List<WebStreamPlaybackState>();
            coordinator.StateChanged += states.Add;

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal("Dispatch feed", coordinator.DisplayName);
            Assert.Equal("https://radio.example.test/feed.mp3", sourceFactory.Source.Options.Url.ToString());
            Assert.Equal("listener", sourceFactory.Source.Options.AuthUsername);
            Assert.Equal("secret", sourceFactory.Source.Options.AuthPassword);
            Assert.Equal(AudioPcm.Console, audioFactory.Output!.Format);
            Assert.Equal("Idle", coordinator.StatusText);
            Assert.True(coordinator.IsActive);

            var pcm = new byte[AudioPcm.FrameBytes];
            pcm[0] = 0x34;
            pcm[1] = 0x12;
            sourceFactory.Source.Emit(pcm);
            pcm[0] = 0;
            pcm[1] = 0;

            Assert.Single(audioFactory.Output.Writes);
            Assert.Equal(0x34, audioFactory.Output.Writes[0][0]);
            Assert.Equal(0x12, audioFactory.Output.Writes[0][1]);
            Assert.Contains(states, state => state.StatusText == "Connecting");
            Assert.Contains(states, state => state.StatusText == "Idle");
            Assert.Contains(states, state => state.StatusText == "RX");

            coordinator.Volume = 2.3;
            Assert.Equal(2.3f, audioFactory.Output.Volume);

            await coordinator.StopAsync();
            await run;
            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task StopAsync_CancelsSourceAndStopsOutput_WithoutDisposingBorrowedFactories()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await coordinator.StopAsync();
            await run;
            await coordinator.DisposeAsync();

            Assert.Equal(1, sourceFactory.Source.StopCount);
            Assert.Equal(1, sourceFactory.Source.DisposeCount);
            Assert.Equal(1, audioFactory.Output!.StopCount);
            Assert.Equal(0, audioFactory.Output.DisposeCount);
            Assert.Equal(0, sourceFactory.DisposeCount);
            Assert.Equal(0, audioFactory.DisposeCount);
            Assert.Equal("Off", coordinator.StatusText);
            Assert.False(coordinator.IsActive);
        }

        [Fact]
        public async Task SourceFailure_TransitionsDown_AndStopsOutput()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sourceFactory.Source.Fail(new InvalidOperationException("decoder failed"));
            await run;

            Assert.Equal("Down", coordinator.StatusText);
            Assert.False(coordinator.IsActive);
            Assert.Equal(1, audioFactory.Output!.StopCount);
            Assert.Equal(1, sourceFactory.Source.DisposeCount);
            Assert.Equal(0, sourceFactory.DisposeCount);
            Assert.Equal(0, audioFactory.DisposeCount);

            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task ExternalStartCancellation_PublishesOffAndInactiveState()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            var states = new List<WebStreamPlaybackState>();
            coordinator.StateChanged += states.Add;
            using var cancellation = new CancellationTokenSource();

            var run = coordinator.StartAsync(cancellation.Token);
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sourceFactory.Source.Emit(new byte[] { 0x34, 0x12 });
            Assert.Equal("RX", coordinator.StatusText);

            cancellation.Cancel();
            await run;

            Assert.Equal("Off", coordinator.StatusText);
            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.IsReceiving);
            Assert.Contains(states, state =>
                state.StatusText == "Off"
                && !state.IsActive
                && !state.IsReceiving);

            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task StopThenStart_CreatesAFreshRun()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);

            var firstRun = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await coordinator.StopAsync();
            await firstRun;

            var firstSource = sourceFactory.Source;
            var secondRun = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.NotSame(firstSource, sourceFactory.Source);
            Assert.True(coordinator.IsActive);

            await coordinator.StopAsync();
            await secondRun;
            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task OutputStartFailure_AllowsAFreshStart()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory { ThrowOnCreateOutput = true };
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);

            await coordinator.StartAsync();
            Assert.Equal("Down", coordinator.StatusText);

            audioFactory.ThrowOnCreateOutput = false;
            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(coordinator.IsActive);

            await coordinator.StopAsync();
            await run;
            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task StopAsync_FromStateChangedCallback_DoesNotSelfJoin()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            Task? stopTask = null;
            coordinator.StateChanged += state =>
            {
                if (state.StatusText == "RX")
                    stopTask = coordinator.StopAsync();
            };

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sourceFactory.Source.Emit(new byte[] { 0x34, 0x12 });

            Assert.NotNull(stopTask);
            Assert.True(stopTask!.IsCompleted);
            await stopTask;
            await run;

            Assert.Equal(1, sourceFactory.Source.StopCount);
            Assert.Equal("Off", coordinator.StatusText);
            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task DisposeAsync_FromStateChangedCallback_DoesNotSelfJoin()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            ValueTask disposeTask = default;
            coordinator.StateChanged += state =>
            {
                if (state.StatusText == "RX")
                    disposeTask = coordinator.DisposeAsync();
            };

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sourceFactory.Source.Emit(new byte[] { 0x34, 0x12 });

            Assert.True(disposeTask.IsCompletedSuccessfully);
            await disposeTask;
            await run;

            Assert.Equal(1, sourceFactory.Source.StopCount);
            Assert.Equal(1, sourceFactory.Source.DisposeCount);
            Assert.Equal("Off", coordinator.StatusText);
        }

        [Fact]
        public async Task ActivityTimer_ExpiresReceivingState_AndOwnsScheduledHandle()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var scheduler = new ManualScheduler();
            var now = DateTime.UtcNow;
            var coordinator = new WebStreamPlaybackCoordinator(
                new Codeplug.WebStream { Name = "Feed", Url = "https://radio.example.test/feed.mp3" },
                sourceFactory,
                audioFactory,
                AudioDeviceId.Default,
                scheduler.Schedule,
                () => now);

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sourceFactory.Source.Emit(new byte[] { 0x34, 0x12 });
            Assert.Equal("RX", coordinator.StatusText);
            Assert.True(coordinator.IsReceiving);
            Assert.Equal(1, scheduler.ScheduledCount);

            now += TimeSpan.FromMilliseconds(1501);
            scheduler.FireNext();

            Assert.Equal("Idle", coordinator.StatusText);
            Assert.False(coordinator.IsReceiving);
            Assert.Equal(0, scheduler.ScheduledCount);

            await coordinator.StopAsync();
            await run;
            await coordinator.DisposeAsync();
            Assert.Equal(0, scheduler.ScheduledCount);
        }

        [Fact]
        public async Task SourceProgress_ProjectsConnectingRetryAndConnectedStates()
        {
            var sourceFactory = new FakeSourceFactory { AutoConnect = false };
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            var states = new List<WebStreamPlaybackState>();
            coordinator.StateChanged += states.Add;

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("Connecting", coordinator.StatusText);
            Assert.DoesNotContain(states, state => state.StatusText == "Idle");

            sourceFactory.Source.EmitProgress(
                new WebStreamSourceProgress(WebStreamSourceProgressKind.Retry, 2));
            Assert.Equal("Retry 2/3", coordinator.StatusText);

            sourceFactory.Source.EmitProgress(
                new WebStreamSourceProgress(WebStreamSourceProgressKind.Connected, 2));
            Assert.Equal("Idle", coordinator.StatusText);

            await coordinator.StopAsync();
            await run;
            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task OutputStartFailure_ReportsDown_AndDisposesCreatedSource()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory { ThrowOnCreateOutput = true };
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            var states = new List<WebStreamPlaybackState>();
            coordinator.StateChanged += states.Add;

            var run = coordinator.StartAsync();
            await run;

            Assert.Equal("Down", coordinator.StatusText);
            Assert.False(coordinator.IsActive);
            Assert.Equal(1, sourceFactory.Source!.DisposeCount);
            Assert.Contains(states, state => state.StatusText == "Down");
            await coordinator.DisposeAsync();
        }

        [Fact]
        public async Task StopAsync_FromJoiningSourceCallback_DoesNotSelfJoin()
        {
            var sourceFactory = new FakeSourceFactory { JoinOnStop = true };
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            Task? stopTask = null;
            coordinator.StateChanged += state =>
            {
                if (state.StatusText == "RX")
                    stopTask = coordinator.StopAsync();
            };

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var emit = Task.Run(() => sourceFactory.Source.Emit(new byte[] { 0x34, 0x12 }));
            await emit.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.NotNull(stopTask);
            Assert.True(stopTask!.IsCompleted);
            sourceFactory.Source.ReleaseStop();
            await run;
            await coordinator.DisposeAsync();
            Assert.Equal(1, sourceFactory.Source.StopCount);
        }

        [Fact]
        public async Task ThrowingStateSubscriber_DoesNotFailPcmRun()
        {
            var sourceFactory = new FakeSourceFactory();
            var audioFactory = new FakeAudioStreamFactory();
            var coordinator = CreateCoordinator(sourceFactory, audioFactory);
            coordinator.StateChanged += _ => throw new InvalidOperationException("observer failed");

            var run = coordinator.StartAsync();
            await sourceFactory.Source!.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            sourceFactory.Source.Emit(new byte[] { 0x34, 0x12 });

            Assert.Equal("RX", coordinator.StatusText);
            Assert.False(run.IsCompleted);

            await coordinator.StopAsync();
            await run;
            await coordinator.DisposeAsync();
        }

        private static WebStreamPlaybackCoordinator CreateCoordinator(
            FakeSourceFactory sourceFactory,
            FakeAudioStreamFactory audioFactory)
            => new(
                new Codeplug.WebStream
                {
                    Name = "Feed",
                    Url = "https://radio.example.test/feed.mp3",
                },
                sourceFactory,
                audioFactory,
                AudioDeviceId.Default);

        private sealed class FakeSourceFactory : IWebStreamSourceFactory
        {
            public FakeSource? Source { get; private set; }
            public int DisposeCount { get; private set; }
            public bool AutoConnect { get; set; } = true;
            public bool JoinOnStop { get; set; }

            public IWebStreamSource Create(WebStreamSourceOptions options)
            {
                Source = new FakeSource(options, AutoConnect, JoinOnStop);
                return Source;
            }

            public void Dispose() => DisposeCount++;
        }

        private sealed class FakeSource : IWebStreamSource
        {
            private readonly TaskCompletionSource<WebStreamSourceResult> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<object?> stopGate =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Action<ReadOnlyMemory<byte>>? onPcm;
            private Action<WebStreamSourceProgress>? onProgress;
            private readonly bool autoConnect;
            private readonly bool joinOnStop;

            public FakeSource(WebStreamSourceOptions options, bool autoConnect, bool joinOnStop)
            {
                Options = options;
                this.autoConnect = autoConnect;
                this.joinOnStop = joinOnStop;
            }

            public WebStreamSourceOptions Options { get; }
            public TaskCompletionSource<bool> Started { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int StopCount { get; private set; }
            public int DisposeCount { get; private set; }

            public Task<WebStreamSourceResult> StartAsync(
                Action<ReadOnlyMemory<byte>> onPcm,
                CancellationToken cancellationToken)
                => StartAsync(onPcm, cancellationToken, null);

            public Task<WebStreamSourceResult> StartAsync(
                Action<ReadOnlyMemory<byte>> onPcm,
                CancellationToken cancellationToken,
                Action<WebStreamSourceProgress>? onProgress = null)
            {
                this.onPcm = onPcm;
                this.onProgress = onProgress;
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                Started.TrySetResult(true);
                if (autoConnect)
                    onProgress?.Invoke(new WebStreamSourceProgress(WebStreamSourceProgressKind.Connected, 1));
                return completion.Task;
            }

            public Task StopAsync()
            {
                StopCount++;
                completion.TrySetCanceled();
                return joinOnStop ? stopGate.Task : Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return ValueTask.CompletedTask;
            }

            public void Emit(byte[] pcm) => onPcm!(pcm);

            public void EmitProgress(WebStreamSourceProgress progress) => onProgress!(progress);

            public void ReleaseStop() => stopGate.TrySetResult(null);

            public void Fail(Exception exception) => completion.TrySetException(exception);
        }

        private sealed class ManualScheduler
        {
            private readonly List<ScheduledAction> _actions = new();

            public int ScheduledCount => _actions.Count;

            public IDisposable Schedule(TimeSpan delay, Action action)
            {
                var scheduled = new ScheduledAction(_actions, action);
                _actions.Add(scheduled);
                return scheduled;
            }

            public void FireNext()
            {
                Assert.NotEmpty(_actions);
                var scheduled = _actions[0];
                _actions.RemoveAt(0);
                scheduled.Fire();
            }

            private sealed class ScheduledAction : IDisposable
            {
                private readonly List<ScheduledAction> _owner;
                private readonly Action _action;
                private bool _disposed;

                public ScheduledAction(List<ScheduledAction> owner, Action action)
                {
                    _owner = owner;
                    _action = action;
                }

                public void Fire()
                {
                    if (!_disposed)
                        _action();
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    _owner.Remove(this);
                }
            }
        }

        private sealed class FakeAudioStreamFactory : IAudioStreamFactory
        {
            public FakeAudioOutput? Output { get; private set; }
            public int DisposeCount { get; private set; }
            public bool ThrowOnCreateOutput { get; set; }

            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
                => throw new NotSupportedException();

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                if (ThrowOnCreateOutput)
                    throw new AudioDeviceException(
                        AudioDeviceErrorKind.DeviceUnavailable,
                        "fake output unavailable");

                Output = new FakeAudioOutput(deviceId, format);
                return Output;
            }

            public IAudioFilePlayer CreateFilePlayer()
                => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeAudioOutput : IAudioOutput, IAsyncDisposable
        {
            public FakeAudioOutput(AudioDeviceId deviceId, PcmFormat format)
            {
                Device = new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Fake output");
                Format = format;
            }

            public AudioDeviceInfo Device { get; }
            public PcmFormat Format { get; }
            public float Volume { get; set; }
            public List<byte[]> Writes { get; } = new();
            public int StopCount { get; private set; }
            public int DisposeCount { get; private set; }

            public AudioWriteResult Write(ReadOnlyMemory<byte> data)
            {
                Writes.Add(data.ToArray());
                return new AudioWriteResult(AudioWriteStatus.Accepted, Writes[^1].Length);
            }

            public void ClearBuffer()
            {
            }

            public Task StopAsync()
            {
                StopCount++;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
