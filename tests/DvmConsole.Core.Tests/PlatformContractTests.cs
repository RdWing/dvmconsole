// SPDX-License-Identifier: AGPL-3.0-only
/**
* Fake-driven platform contract gate for the DvmConsole.Platform abstraction
* surface (audio streams, device catalogs, file dialogs, global hotkeys,
* native-library probing and the PlatformServices composition root).
*
* This file is the contract-test half of a contract-first workflow: it is written
* entirely against the agreed platform contract and now exercises the production
* contracts in DvmConsole.Platform. Nothing here depends on hardware, native
* libraries, files, secrets or UI frameworks.
*
* The fakes in this file are the reference implementation of the contract:
* they encode the agreed semantics (typed stream ends, volume clamping,
* cancellation returning IsCancelled results instead of throwing OCE,
* idempotent stop/unregister/dispose, logical-only native probe names,
* framework-only assembly references) so the tests exercise the surface
* non-vacuously. When the production contracts land, these tests compile
* and the fakes can be swapped for real implementations.
*/
#nullable enable
using DvmConsole.Platform;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Dialogs;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Native;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Fake-driven contract tests for the DvmConsole.Platform surface.
    /// </summary>
    public static class PlatformContractTests
    {
        // ------------------------------------------------------------------
        // Fakes: reference implementations of the platform contracts.
        // ------------------------------------------------------------------

        private sealed class FakeAudioInput : IAudioInput
        {
            private readonly TaskCompletionSource<AudioStreamEnd> _end =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenSource? _cts;
            private int _started;

            public FakeAudioInput(AudioDeviceInfo device, PcmFormat format)
            {
                Device = device;
                Format = format;
            }

            public AudioDeviceInfo Device { get; }

            public PcmFormat Format { get; }

            public int CallbackCount { get; private set; }

            public Task<AudioStreamEnd> StartAsync(
                Func<ReadOnlyMemory<byte>, Task> onData,
                CancellationToken cancellationToken)
            {
                if (onData is null)
                {
                    throw new ArgumentNullException(nameof(onData));
                }

                if (Interlocked.Exchange(ref _started, 1) != 0)
                {
                    throw new InvalidOperationException("Stream already started.");
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _cts = cts;
                cancellationToken.Register(() => Complete(AudioStreamEnd.Cancelled()));

                _ = Task.Run(async () =>
                {
                    var frame = new byte[AudioPcm.FrameBytes];
                    try
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            cts.Token.ThrowIfCancellationRequested();
                            CallbackCount++;
                            await onData(frame);
                            await Task.Delay(1);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation path: the typed end is produced by the token registration.
                    }
                    catch (Exception error)
                    {
                        Complete(AudioStreamEnd.Error(AudioDeviceErrorKind.ReadFailed, error.Message));
                    }
                });

                return _end.Task;
            }

            public Task StopAsync()
            {
                _cts?.Cancel();
                Complete(AudioStreamEnd.Requested());
                return Task.CompletedTask;
            }

            public void SimulateDeviceLost()
            {
                _cts?.Cancel();
                Complete(AudioStreamEnd.DeviceLost());
            }

            private void Complete(AudioStreamEnd end) => _end.TrySetResult(end);
        }

        private sealed class FakeAudioOutput : IAudioOutput
        {
            private readonly int _capacity;
            private float _volume = 1f;
            private bool _started = true;
            private bool _deviceLost;
            private int _buffered;

            public FakeAudioOutput(AudioDeviceInfo device, PcmFormat format, int capacity)
            {
                Device = device;
                Format = format;
                _capacity = capacity;
            }

            public AudioDeviceInfo Device { get; }

            public PcmFormat Format { get; }

            public float Volume
            {
                get => _volume;
                set => _volume = Math.Clamp(value, 0f, 1f);
            }

            public AudioWriteResult Write(ReadOnlyMemory<byte> data)
            {
                if (_deviceLost)
                {
                    return new AudioWriteResult(AudioWriteStatus.DeviceLost, _buffered);
                }

                if (!_started)
                {
                    return new AudioWriteResult(AudioWriteStatus.NotStarted, _buffered);
                }

                if (_buffered + data.Length > _capacity)
                {
                    return new AudioWriteResult(AudioWriteStatus.BufferOverflow, _buffered);
                }

                _buffered += data.Length;
                return new AudioWriteResult(AudioWriteStatus.Accepted, _buffered);
            }

            public void ClearBuffer() => _buffered = 0;

            public Task StopAsync()
            {
                _started = false;
                return Task.CompletedTask;
            }

            public void SimulateDeviceLost() => _deviceLost = true;
        }

        private sealed class FakeAudioFilePlayer : IAudioFilePlayer
        {
            private bool _failNext;

            public Task<AudioPlaybackResult> PlayPcmAsync(string filePath, CancellationToken cancellationToken)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    throw new ArgumentException("A file path is required.", nameof(filePath));
                }

                if (_failNext)
                {
                    _failNext = false;
                    return Task.FromResult(new AudioPlaybackResult(AudioPlaybackOutcome.Failed, "Simulated playback failure."));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromResult(new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null));
                }

                return Task.FromResult(new AudioPlaybackResult(AudioPlaybackOutcome.Completed, null));
            }

            public Task StopAsync() => Task.CompletedTask;

            public void SimulateFailure() => _failNext = true;
        }

        private sealed class FakeDeviceCatalog : IAudioDeviceCatalog
        {
            private readonly IReadOnlyList<AudioDeviceInfo> _inputs;
            private readonly IReadOnlyList<AudioDeviceInfo> _outputs;

            public FakeDeviceCatalog(
                IReadOnlyList<AudioDeviceInfo> inputs,
                IReadOnlyList<AudioDeviceInfo> outputs)
            {
                _inputs = inputs;
                _outputs = outputs;
            }

            public IReadOnlyList<AudioDeviceInfo> GetInputs() => _inputs;

            public IReadOnlyList<AudioDeviceInfo> GetOutputs() => _outputs;

            public AudioDeviceInfo? GetDefaultInput() => _inputs.FirstOrDefault(device => device.Id.IsDefault);

            public AudioDeviceInfo? GetDefaultOutput() => _outputs.FirstOrDefault(device => device.Id.IsDefault);

            public bool TryFind(AudioDeviceId id, out AudioDeviceInfo? device)
            {
                device = _inputs.Concat(_outputs).FirstOrDefault(candidate => candidate.Id == id);
                return device is not null;
            }

            public bool DisposedAsync { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposedAsync = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeAudioStreamFactory : IAudioStreamFactory
        {
            public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
                => new FakeAudioInput(new AudioDeviceInfo(deviceId, AudioDeviceDirection.Input, "Fake input"), format);

            public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
                => new FakeAudioOutput(new AudioDeviceInfo(deviceId, AudioDeviceDirection.Output, "Fake output"), format, AudioPcm.BlockBytes);

            public IAudioFilePlayer CreateFilePlayer() => new FakeAudioFilePlayer();

            public bool DisposedAsync { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposedAsync = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeFileDialogService : IFileDialogService
        {
            private string? _selection;
            private IReadOnlyList<string>? _selections;
            private bool _cancelNext;

            public void SimulateSelection(string path)
            {
                _selection = path;
                _selections = null;
                _cancelNext = false;
            }

            public void SimulateSelections(params string[] paths)
            {
                _selections = paths;
                _selection = paths.FirstOrDefault();
                _cancelNext = false;
            }

            public void SimulateCancel() => _cancelNext = true;

            public Task<FileDialogResult> OpenFileAsync(OpenFileRequest request, CancellationToken cancellationToken)
            {
                if (request is null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                if (request.Filters is null)
                {
                    throw new ArgumentNullException(nameof(request.Filters));
                }

                if (cancellationToken.IsCancellationRequested || _cancelNext)
                {
                    return Task.FromResult(FileDialogResult.Cancelled());
                }

                if (request.AllowMultiple && _selections is not null)
                {
                    return Task.FromResult(FileDialogResult.FromSelections(_selections));
                }

                return Task.FromResult(FileDialogResult.FromSelection(_selection ?? string.Empty));
            }

            public Task<FileDialogResult> SaveFileAsync(SaveFileRequest request, CancellationToken cancellationToken)
            {
                if (request is null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                if (request.Filters is null)
                {
                    throw new ArgumentNullException(nameof(request.Filters));
                }

                if (cancellationToken.IsCancellationRequested || _cancelNext)
                {
                    return Task.FromResult(FileDialogResult.Cancelled());
                }

                return Task.FromResult(FileDialogResult.FromSelection(_selection ?? string.Empty));
            }

            public Task<FolderDialogResult> PickFolderAsync(FolderPickerRequest request, CancellationToken cancellationToken)
            {
                if (request is null)
                {
                    throw new ArgumentNullException(nameof(request));
                }

                if (cancellationToken.IsCancellationRequested || _cancelNext)
                {
                    return Task.FromResult(FolderDialogResult.Cancelled());
                }

                return Task.FromResult(FolderDialogResult.FromSelection(_selection ?? string.Empty));
            }

            public bool DisposedAsync { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposedAsync = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeGlobalHotkeyService : IGlobalHotkeyService
        {
            private readonly Dictionary<HotkeyGesture, HotkeyCapability> _capabilities = new();
            private readonly HashSet<HotkeyGesture> _registered = new();
            private bool _disposed;

            public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

            public bool Disposed { get; private set; }

            public void SetCapability(HotkeyGesture gesture, HotkeyCapability capability)
                => _capabilities[gesture] = capability;

            public HotkeyCapability GetCapability(HotkeyGesture gesture)
                => _capabilities.TryGetValue(gesture, out var capability) ? capability : HotkeyCapability.Unsupported;

            public Task<HotkeyRegistrationResult> RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
            {
                switch (GetCapability(gesture))
                {
                    case HotkeyCapability.Unsupported:
                        return Task.FromResult(new HotkeyRegistrationResult(HotkeyRegistrationStatus.Unsupported, gesture));
                    case HotkeyCapability.PermissionRequired:
                        return Task.FromResult(new HotkeyRegistrationResult(HotkeyRegistrationStatus.PermissionDenied, gesture));
                    case HotkeyCapability.Available:
                        if (!_registered.Add(gesture))
                        {
                            return Task.FromResult(new HotkeyRegistrationResult(HotkeyRegistrationStatus.AlreadyRegistered, gesture));
                        }

                        return Task.FromResult(new HotkeyRegistrationResult(HotkeyRegistrationStatus.Registered, gesture));
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            public Task UnregisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
            {
                _registered.Remove(gesture);
                return Task.CompletedTask;
            }

            public void SimulatePress(HotkeyGesture gesture)
                => HotkeyPressed?.Invoke(this, new HotkeyEventArgs(gesture, HotkeyEventType.Pressed));

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Disposed = true;
                HotkeyPressed = null;
            }
        }

        private sealed class FakeNativeLibraryProbe : INativeLibraryProbe
        {
            private readonly HashSet<string> _exports = new(StringComparer.Ordinal);
            private int _calls;

            public int ProbeCalls => _calls;

            public void AddExport(string exportName) => _exports.Add(exportName);

            public NativeLibraryProbeResult Probe(string logicalName, IReadOnlyList<string> requiredExports)
            {
                _calls++;
                if (string.IsNullOrWhiteSpace(logicalName))
                {
                    throw new ArgumentException("A logical library name is required.", nameof(logicalName));
                }

                if (requiredExports is null)
                {
                    throw new ArgumentNullException(nameof(requiredExports));
                }

                if (requiredExports.Count == 0)
                {
                    throw new ArgumentException("At least one required export must be declared.", nameof(requiredExports));
                }

                if (LooksLikeFileName(logicalName))
                {
                    throw new ArgumentException(
                        "Probe takes a logical library name, never a file name with an extension.", nameof(logicalName));
                }

                var missing = requiredExports.Where(export => !_exports.Contains(export)).ToArray();
                return missing.Length == 0
                    ? NativeLibraryProbeResult.Success(logicalName)
                    : NativeLibraryProbeResult.Failure(
                        logicalName,
                        $"Missing required export(s): {string.Join(", ", missing)}");
            }

            public bool DisposedAsync { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposedAsync = true;
                return ValueTask.CompletedTask;
            }

            private static bool LooksLikeFileName(string logicalName)
                => logicalName.Contains(".dll", StringComparison.OrdinalIgnoreCase)
                    || logicalName.Contains(".so", StringComparison.OrdinalIgnoreCase)
                    || logicalName.Contains(".dylib", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // PCM format and stream framing contract.
        // ------------------------------------------------------------------

        public sealed class AudioPcmContractTests
        {
            /// <summary>
            /// AudioPcm.Console is the locked console codec: 8000 Hz, 16-bit, mono.
            /// </summary>
            [Fact]
            public void ConsoleFormat_IsEightKilohertzMono16Bit()
            {
                Assert.Equal(8000, AudioPcm.Console.SampleRate);
                Assert.Equal(16, AudioPcm.Console.BitsPerSample);
                Assert.Equal(1, AudioPcm.Console.Channels);
                Assert.Equal(new PcmFormat(8000, 16, 1), AudioPcm.Console);
            }

            /// <summary>
            /// Byte metrics derive from the format: 16-bit mono at 8 kHz is 2
            /// bytes per sample and 16000 bytes per second; 16-bit stereo at
            /// 44.1 kHz is 176400 bytes per second.
            /// </summary>
            [Fact]
            public void Formats_ComputeByteMetrics()
            {
                Assert.Equal(2, AudioPcm.Console.BytesPerSample);
                Assert.Equal(16000, AudioPcm.Console.BytesPerSecond);

                var stereo44100 = new PcmFormat(44100, 16, 2);
                Assert.Equal(2, stereo44100.BytesPerSample);
                Assert.Equal(176400, stereo44100.BytesPerSecond);
            }

            /// <summary>
            /// The 20 ms frame (320 bytes) and 100 ms block (1600 bytes) sizes
            /// are locked, with the block exactly five frames.
            /// </summary>
            [Fact]
            public void FrameAndBlockSizes_AreLocked()
            {
                Assert.Equal(320, AudioPcm.FrameBytes);
                Assert.Equal(1600, AudioPcm.BlockBytes);
                Assert.Equal(5 * AudioPcm.FrameBytes, AudioPcm.BlockBytes);
            }

            /// <summary>
            /// FrameCount covers a byte count with ceiling division: partial
            /// frames count as one, so 1 byte needs one frame and 321 bytes
            /// needs two.
            /// </summary>
            [Fact]
            public void FrameCountAndAlignment_AreCeilingBased()
            {
                Assert.Equal(0, AudioPcm.FrameCount(0));
                Assert.Equal(1, AudioPcm.FrameCount(1));
                Assert.Equal(1, AudioPcm.FrameCount(319));
                Assert.Equal(1, AudioPcm.FrameCount(320));
                Assert.Equal(2, AudioPcm.FrameCount(321));
                Assert.Equal(5, AudioPcm.FrameCount(1600));
                Assert.Equal(6, AudioPcm.FrameCount(1601));

                Assert.True(AudioPcm.IsFrameAligned(0));
                Assert.True(AudioPcm.IsFrameAligned(320));
                Assert.True(AudioPcm.IsFrameAligned(1600));
                Assert.False(AudioPcm.IsFrameAligned(319));
                Assert.False(AudioPcm.IsFrameAligned(321));
            }
        }

        // ------------------------------------------------------------------
        // Device identity and catalog contract.
        // ------------------------------------------------------------------

        public sealed class DeviceContractTests
        {
            /// <summary>
            /// AudioDeviceId.Default is the empty default device marker.
            /// </summary>
            [Fact]
            public void DefaultDeviceId_IsEmptyAndDefault()
            {
                Assert.True(AudioDeviceId.Default.IsDefault);
                Assert.True(AudioDeviceId.Default.IsEmpty);
                Assert.Equal(string.Empty, AudioDeviceId.Default.Value);
                Assert.Equal(new AudioDeviceId(string.Empty, true), AudioDeviceId.Default);
            }

            /// <summary>
            /// FromKey builds a non-default device id from a key, and rejects
            /// null or whitespace keys.
            /// </summary>
            [Fact]
            public void FromKey_BuildsNonDefaultId_AndRejectsWhitespace()
            {
                var id = AudioDeviceId.FromKey("mic-1");
                Assert.Equal("mic-1", id.Value);
                Assert.False(id.IsDefault);
                Assert.False(id.IsEmpty);
                Assert.Equal(new AudioDeviceId("mic-1", false), id);

                Assert.ThrowsAny<ArgumentException>(() => AudioDeviceId.FromKey(null!));
                Assert.ThrowsAny<ArgumentException>(() => AudioDeviceId.FromKey(string.Empty));
                Assert.ThrowsAny<ArgumentException>(() => AudioDeviceId.FromKey("   "));
            }

            /// <summary>
            /// AudioDeviceInfo carries the id, direction and human name of a device.
            /// </summary>
            [Fact]
            public void DeviceInfo_ExposesIdentity()
            {
                var id = AudioDeviceId.FromKey("mic-1");
                var info = new AudioDeviceInfo(id, AudioDeviceDirection.Input, "Built-in Microphone");

                Assert.Equal(id, info.Id);
                Assert.Equal(AudioDeviceDirection.Input, info.Direction);
                Assert.Equal("Built-in Microphone", info.Name);
            }

            /// <summary>
            /// The catalog enumerates inputs and outputs, exposes the default
            /// devices, and resolves ids through TryFind.
            /// </summary>
            [Fact]
            public void Catalog_EnumeratesAndResolvesDevices()
            {
                var defaultInId = new AudioDeviceId("default-in", true);
                var secondInId = AudioDeviceId.FromKey("second-in");
                var defaultOutId = new AudioDeviceId("default-out", true);
                var secondOutId = AudioDeviceId.FromKey("second-out");

                var catalog = new FakeDeviceCatalog(
                    new[]
                    {
                        new AudioDeviceInfo(defaultInId, AudioDeviceDirection.Input, "Default input"),
                        new AudioDeviceInfo(secondInId, AudioDeviceDirection.Input, "Second input"),
                    },
                    new[]
                    {
                        new AudioDeviceInfo(defaultOutId, AudioDeviceDirection.Output, "Default output"),
                        new AudioDeviceInfo(secondOutId, AudioDeviceDirection.Output, "Second output"),
                    });

                Assert.Equal(2, catalog.GetInputs().Count);
                Assert.Equal(2, catalog.GetOutputs().Count);
                Assert.Equal(defaultInId, catalog.GetDefaultInput()!.Id);
                Assert.Equal(defaultOutId, catalog.GetDefaultOutput()!.Id);
                Assert.Equal(AudioDeviceDirection.Output, catalog.GetOutputs()[1].Direction);

                Assert.True(catalog.TryFind(defaultInId, out var found));
                Assert.Equal("Default input", found!.Name);
                Assert.False(catalog.TryFind(AudioDeviceId.FromKey("missing"), out var missing));
                Assert.Null(missing);
            }
        }

        // ------------------------------------------------------------------
        // Audio input lifecycle contract.
        // ------------------------------------------------------------------

        public sealed class InputContractTests
        {
            private static readonly AudioDeviceInfo FakeInputDevice = new(
                AudioDeviceId.FromKey("fake-input"),
                AudioDeviceDirection.Input,
                "Fake input");

            /// <summary>
            /// StartAsync rejects a null data callback up front.
            /// </summary>
            [Fact]
            public void StartAsync_NullCallback_Throws()
            {
                var input = new FakeAudioInput(FakeInputDevice, AudioPcm.Console);

                Assert.Throws<ArgumentNullException>(() =>
                {
                    _ = input.StartAsync(null!, CancellationToken.None);
                });
            }

            /// <summary>
            /// StopAsync ends the long-running stream with Requested and is
            /// idempotent, including when called before the stream starts.
            /// </summary>
            [Fact]
            public async Task Stop_EndsWithRequested_AndIsIdempotent()
            {
                var input = new FakeAudioInput(FakeInputDevice, AudioPcm.Console);
                var started = input.StartAsync(_ => Task.CompletedTask, CancellationToken.None);

                await input.StopAsync();
                await input.StopAsync();

                var end = await started.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AudioStreamStopReason.Requested, end.StopReason);

                var neverStarted = new FakeAudioInput(FakeInputDevice, AudioPcm.Console);
                await neverStarted.StopAsync();
                await neverStarted.StopAsync();
            }

            /// <summary>
            /// A pre-cancelled token ends the stream with Cancelled instead of
            /// throwing OperationCanceledException at the caller.
            /// </summary>
            [Fact]
            public async Task PreCancelledToken_EndsWithCancelled()
            {
                var input = new FakeAudioInput(FakeInputDevice, AudioPcm.Console);
                var started = input.StartAsync(_ => Task.CompletedTask, new CancellationToken(canceled: true));

                var end = await started.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AudioStreamStopReason.Cancelled, end.StopReason);
            }

            /// <summary>
            /// Device loss surfaces as a typed DeviceLost end.
            /// </summary>
            [Fact]
            public async Task DeviceLoss_EndsWithDeviceLost()
            {
                var input = new FakeAudioInput(FakeInputDevice, AudioPcm.Console);
                var started = input.StartAsync(_ => Task.CompletedTask, CancellationToken.None);

                input.SimulateDeviceLost();

                var end = await started.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AudioStreamStopReason.DeviceLost, end.StopReason);
            }

            /// <summary>
            /// An exception inside the data callback ends the stream with Error
            /// and carries diagnostic details.
            /// </summary>
            [Fact]
            public async Task CallbackError_EndsWithError()
            {
                var input = new FakeAudioInput(FakeInputDevice, AudioPcm.Console);
                var started = input.StartAsync(
                    _ => throw new InvalidOperationException("consumer failure"),
                    CancellationToken.None);

                var end = await started.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AudioStreamStopReason.Error, end.StopReason);
                Assert.NotNull(end.ErrorKind);
                Assert.False(string.IsNullOrWhiteSpace(end.ErrorMessage));
            }

            /// <summary>
            /// AudioStreamEnd statics and AudioDeviceException carry the typed
            /// stop/error details the app switches on.
            /// </summary>
            [Fact]
            public void StreamEndAndException_CarryErrorDetails()
            {
                Assert.Equal(AudioStreamStopReason.Requested, AudioStreamEnd.Requested().StopReason);
                Assert.Equal(AudioStreamStopReason.Cancelled, AudioStreamEnd.Cancelled().StopReason);
                Assert.Equal(AudioStreamStopReason.DeviceLost, AudioStreamEnd.DeviceLost().StopReason);

                var error = AudioStreamEnd.Error(AudioDeviceErrorKind.DeviceUnavailable, "no device");
                Assert.Equal(AudioStreamStopReason.Error, error.StopReason);
                Assert.Equal(AudioDeviceErrorKind.DeviceUnavailable, error.ErrorKind);
                Assert.Equal("no device", error.ErrorMessage);

                var exception = new AudioDeviceException(AudioDeviceErrorKind.OpenFailed, "open failed");
                Assert.Equal(AudioDeviceErrorKind.OpenFailed, exception.Kind);
                Assert.Equal("open failed", exception.Message);
            }

            /// <summary>
            /// The stream factory creates typed input/output/file-player objects
            /// carrying the requested device and format.
            /// </summary>
            [Fact]
            public void StreamFactory_CreatesTypedStreams()
            {
                var factory = new FakeAudioStreamFactory();
                var deviceId = AudioDeviceId.FromKey("stream-fake");

                var input = factory.CreateInput(deviceId, AudioPcm.Console);
                Assert.IsType<FakeAudioInput>(input);
                Assert.Equal(deviceId, input.Device.Id);
                Assert.Equal(AudioPcm.Console, input.Format);

                var output = factory.CreateOutput(deviceId, AudioPcm.Console);
                Assert.IsType<FakeAudioOutput>(output);
                Assert.Equal(deviceId, output.Device.Id);
                Assert.Equal(AudioPcm.Console, output.Format);

                Assert.IsType<FakeAudioFilePlayer>(factory.CreateFilePlayer());
            }
        }

        // ------------------------------------------------------------------
        // Audio output contract.
        // ------------------------------------------------------------------

        public sealed class OutputContractTests
        {
            private static readonly AudioDeviceInfo FakeOutputDevice = new(
                AudioDeviceId.FromKey("fake-output"),
                AudioDeviceDirection.Output,
                "Fake output");

            private static readonly byte[] OneFrame = new byte[AudioPcm.FrameBytes];

            /// <summary>
            /// Writes are accepted while there is room, report the buffered byte
            /// count, and switch to BufferOverflow once the buffer is full.
            /// </summary>
            [Fact]
            public void Write_AcceptsThenOverflows()
            {
                var output = new FakeAudioOutput(FakeOutputDevice, AudioPcm.Console, AudioPcm.BlockBytes);

                var first = output.Write(OneFrame);
                Assert.Equal(AudioWriteStatus.Accepted, first.Status);
                Assert.Equal(AudioPcm.FrameBytes, first.BufferedBytes);

                var second = output.Write(OneFrame);
                Assert.Equal(AudioWriteStatus.Accepted, second.Status);
                Assert.Equal(2 * AudioPcm.FrameBytes, second.BufferedBytes);

                var overflow = output.Write(new byte[AudioPcm.BlockBytes]);
                Assert.Equal(AudioWriteStatus.BufferOverflow, overflow.Status);
                Assert.Equal(2 * AudioPcm.FrameBytes, overflow.BufferedBytes);
            }

            /// <summary>
            /// ClearBuffer resets accounting so writes are accepted again.
            /// </summary>
            [Fact]
            public void ClearBuffer_ResetsAccounting()
            {
                var output = new FakeAudioOutput(FakeOutputDevice, AudioPcm.Console, AudioPcm.BlockBytes);
                output.Write(OneFrame);

                output.ClearBuffer();

                var after = output.Write(OneFrame);
                Assert.Equal(AudioWriteStatus.Accepted, after.Status);
                Assert.Equal(AudioPcm.FrameBytes, after.BufferedBytes);
            }

            /// <summary>
            /// Writes after stop report NotStarted and after device loss report
            /// DeviceLost, never throwing.
            /// </summary>
            [Fact]
            public async Task Write_AfterStopOrDeviceLoss_ReportsStatus()
            {
                var stopped = new FakeAudioOutput(FakeOutputDevice, AudioPcm.Console, AudioPcm.BlockBytes);
                await stopped.StopAsync();

                Assert.Equal(AudioWriteStatus.NotStarted, stopped.Write(OneFrame).Status);

                var lost = new FakeAudioOutput(FakeOutputDevice, AudioPcm.Console, AudioPcm.BlockBytes);
                lost.SimulateDeviceLost();

                Assert.Equal(AudioWriteStatus.DeviceLost, lost.Write(OneFrame).Status);
            }

            /// <summary>
            /// Volume is clamped to the unit interval on assignment.
            /// </summary>
            [Fact]
            public void Volume_ClampsToUnitInterval()
            {
                var output = new FakeAudioOutput(FakeOutputDevice, AudioPcm.Console, AudioPcm.BlockBytes);

                Assert.Equal(1f, output.Volume);

                output.Volume = 1.5f;
                Assert.Equal(1f, output.Volume);

                output.Volume = -0.5f;
                Assert.Equal(0f, output.Volume);

                output.Volume = 0.5f;
                Assert.Equal(0.5f, output.Volume);
            }

            /// <summary>
            /// AudioWriteResult is a value type: status and buffered bytes
            /// compare by value and deconstruct in order.
            /// </summary>
            [Fact]
            public void WriteResult_ValueEqualityAndDeconstruction()
            {
                var first = new AudioWriteResult(AudioWriteStatus.Accepted, 320);
                var second = new AudioWriteResult(AudioWriteStatus.Accepted, 320);
                var other = new AudioWriteResult(AudioWriteStatus.BufferOverflow, 320);

                Assert.Equal(first, second);
                Assert.NotEqual(first, other);

                var (status, buffered) = first;
                Assert.Equal(AudioWriteStatus.Accepted, status);
                Assert.Equal(320, buffered);
            }
        }

        // ------------------------------------------------------------------
        // File player contract.
        // ------------------------------------------------------------------

        public sealed class FilePlayerContractTests
        {
            /// <summary>
            /// PlayPcmAsync rejects null, empty and whitespace paths with
            /// ArgumentException (synchronously or as a faulted task).
            /// </summary>
            [Fact]
            public async Task PlayPcmAsync_EmptyOrNullPath_Throws()
            {
                var player = new FakeAudioFilePlayer();

                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => player.PlayPcmAsync(string.Empty, CancellationToken.None));
                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => player.PlayPcmAsync("   ", CancellationToken.None));
                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => player.PlayPcmAsync(null!, CancellationToken.None));
            }

            /// <summary>
            /// A valid play runs to completion with outcome Completed, and
            /// cancellation yields outcome Cancelled instead of an exception.
            /// </summary>
            [Fact]
            public async Task Play_CompletesAndCancels()
            {
                var player = new FakeAudioFilePlayer();

                var completed = await player.PlayPcmAsync("/tmp/recording.pcm", CancellationToken.None);
                Assert.Equal(AudioPlaybackOutcome.Completed, completed.Outcome);
                Assert.Null(completed.ErrorMessage);

                var cancelled = await player.PlayPcmAsync("/tmp/recording.pcm", new CancellationToken(canceled: true));
                Assert.Equal(AudioPlaybackOutcome.Cancelled, cancelled.Outcome);
            }

            /// <summary>
            /// A failed playback reports outcome Failed with a diagnostic
            /// message, and StopAsync is an idempotent no-op.
            /// </summary>
            [Fact]
            public async Task Play_FailureReportsError_AndStopIsIdempotent()
            {
                var player = new FakeAudioFilePlayer();
                player.SimulateFailure();

                var failed = await player.PlayPcmAsync("/tmp/recording.pcm", CancellationToken.None);
                Assert.Equal(AudioPlaybackOutcome.Failed, failed.Outcome);
                Assert.False(string.IsNullOrWhiteSpace(failed.ErrorMessage));

                await player.StopAsync();
                await player.StopAsync();
            }
        }

        // ------------------------------------------------------------------
        // File dialog contract.
        // ------------------------------------------------------------------

        public sealed class DialogContractTests
        {
            /// <summary>
            /// Request types and filters expose their full field surface.
            /// </summary>
            [Fact]
            public void RequestsAndFilter_ExposeFields()
            {
                var filter = new FileDialogFilter("WAV files", new[] { "*.wav" });
                Assert.Equal("WAV files", filter.Name);
                Assert.Equal(new[] { "*.wav" }, filter.Patterns);

                var open = new OpenFileRequest(
                    Title: "Open recording",
                    Filters: new[] { filter },
                    AllowMultiple: true,
                    InitialDirectory: "/tmp");
                Assert.Equal("Open recording", open.Title);
                Assert.Same(filter, open.Filters[0]);
                Assert.True(open.AllowMultiple);
                Assert.Equal("/tmp", open.InitialDirectory);

                var save = new SaveFileRequest(
                    Title: "Save recording",
                    Filters: new[] { filter },
                    DefaultFileName: "out.pcm",
                    InitialDirectory: "/tmp");
                Assert.Equal("Save recording", save.Title);
                Assert.Equal("out.pcm", save.DefaultFileName);

                var folder = new FolderPickerRequest(Title: "Pick folder", InitialDirectory: "/tmp");
                Assert.Equal("Pick folder", folder.Title);
                Assert.Equal("/tmp", folder.InitialDirectory);
            }

            /// <summary>
            /// Single and multiple selections populate Selected and SelectedMany.
            /// </summary>
            [Fact]
            public async Task Open_SingleAndMultipleSelections()
            {
                var service = new FakeFileDialogService();
                var request = new OpenFileRequest(null, new[] { new FileDialogFilter("PCM", new[] { "*.pcm" }) }, AllowMultiple: true, null);

                service.SimulateSelection("/tmp/a.pcm");
                var single = await service.OpenFileAsync(request, CancellationToken.None);
                Assert.Equal("/tmp/a.pcm", single.Selected);
                Assert.Equal(new[] { "/tmp/a.pcm" }, single.SelectedMany);
                Assert.False(single.Cancelled);

                service.SimulateSelections("/tmp/a.pcm", "/tmp/b.pcm");
                var many = await service.OpenFileAsync(request, CancellationToken.None);
                Assert.Equal(new[] { "/tmp/a.pcm", "/tmp/b.pcm" }, many.SelectedMany);
                Assert.Equal("/tmp/a.pcm", many.Selected);
                Assert.False(many.Cancelled);
            }

            /// <summary>
            /// A cancelled dialog returns a Cancelled result with no selection,
            /// and a cancelled token produces the same result instead of an
            /// OperationCanceledException.
            /// </summary>
            [Fact]
            public async Task Open_CancelledByDialogOrToken()
            {
                var service = new FakeFileDialogService();
                var request = new OpenFileRequest(null, new[] { new FileDialogFilter("PCM", new[] { "*.pcm" }) }, AllowMultiple: false, null);

                service.SimulateCancel();
                var byDialog = await service.OpenFileAsync(request, CancellationToken.None);
                Assert.True(byDialog.Cancelled);
                Assert.Null(byDialog.Selected);
                Assert.Empty(byDialog.SelectedMany);

                var byToken = await service.OpenFileAsync(request, new CancellationToken(canceled: true));
                Assert.True(byToken.Cancelled);
                Assert.Null(byToken.Selected);
            }

            /// <summary>
            /// The dialog service validates its inputs.
            /// </summary>
            [Fact]
            public async Task NullRequestOrFilters_Throw()
            {
                var service = new FakeFileDialogService();

                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => service.OpenFileAsync(null!, CancellationToken.None));
                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => service.SaveFileAsync(null!, CancellationToken.None));
                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => service.PickFolderAsync(null!, CancellationToken.None));

                var noFilters = new OpenFileRequest(null, null!, AllowMultiple: false, null);
                await Assert.ThrowsAnyAsync<ArgumentException>(
                    () => service.OpenFileAsync(noFilters, CancellationToken.None));
            }

            /// <summary>
            /// Save and folder pickers return their selection, or a Cancelled
            /// result when dismissed.
            /// </summary>
            [Fact]
            public async Task SaveAndFolderDialogs_ReturnSelectionsOrCancelled()
            {
                var service = new FakeFileDialogService();
                var saveRequest = new SaveFileRequest(null, new[] { new FileDialogFilter("PCM", new[] { "*.pcm" }) }, "out.pcm", null);
                var folderRequest = new FolderPickerRequest(null, null);

                service.SimulateSelection("/tmp/out.pcm");
                var saved = await service.SaveFileAsync(saveRequest, CancellationToken.None);
                Assert.Equal("/tmp/out.pcm", saved.Selected);
                Assert.False(saved.Cancelled);

                service.SimulateSelection("/tmp/recordings");
                var picked = await service.PickFolderAsync(folderRequest, CancellationToken.None);
                Assert.Equal("/tmp/recordings", picked.Selected);
                Assert.False(picked.Cancelled);

                service.SimulateCancel();
                var dismissed = await service.PickFolderAsync(folderRequest, CancellationToken.None);
                Assert.True(dismissed.Cancelled);
                Assert.Null(dismissed.Selected);
            }
        }

        // ------------------------------------------------------------------
        // Global hotkey contract.
        // ------------------------------------------------------------------

        public sealed class HotkeyContractTests
        {
            private static readonly HotkeyGesture DefaultGesture =
                new(HotkeyKey.F1, HotkeyModifiers.Control | HotkeyModifiers.Shift);

            /// <summary>
            /// Gestures compare by value and modifiers behave as flags.
            /// </summary>
            [Fact]
            public void Gesture_EqualityAndModifierFlags()
            {
                var same = new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control | HotkeyModifiers.Shift);
                var different = new HotkeyGesture(HotkeyKey.F1, HotkeyModifiers.Control);

                Assert.Equal(DefaultGesture, same);
                Assert.NotEqual(DefaultGesture, different);

                var modifiers = DefaultGesture.Modifiers;
                Assert.True(modifiers.HasFlag(HotkeyModifiers.Control));
                Assert.True(modifiers.HasFlag(HotkeyModifiers.Shift));
                Assert.False(modifiers.HasFlag(HotkeyModifiers.Alt));
                Assert.NotEqual(HotkeyModifiers.None, modifiers);
            }

            /// <summary>
            /// Capability is reported per gesture, with Unsupported as the
            /// default for gestures that were never declared.
            /// </summary>
            [Fact]
            public void Capability_ReportedPerGesture()
            {
                var service = new FakeGlobalHotkeyService();
                var permission = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None);
                var unsupported = new HotkeyGesture(HotkeyKey.F5, HotkeyModifiers.Alt);

                service.SetCapability(DefaultGesture, HotkeyCapability.Available);
                service.SetCapability(permission, HotkeyCapability.PermissionRequired);

                Assert.Equal(HotkeyCapability.Available, service.GetCapability(DefaultGesture));
                Assert.Equal(HotkeyCapability.PermissionRequired, service.GetCapability(permission));
                Assert.Equal(HotkeyCapability.Unsupported, service.GetCapability(unsupported));
            }

            /// <summary>
            /// Registration reports Registered for a new gesture, then
            /// AlreadyRegistered for a duplicate; permission-required and
            /// unsupported gestures report PermissionDenied and Unsupported.
            /// </summary>
            [Fact]
            public async Task Registration_NewDuplicateDeniedUnsupported()
            {
                var service = new FakeGlobalHotkeyService();
                service.SetCapability(DefaultGesture, HotkeyCapability.Available);
                var permission = new HotkeyGesture(HotkeyKey.A, HotkeyModifiers.None);
                service.SetCapability(permission, HotkeyCapability.PermissionRequired);
                var unsupported = new HotkeyGesture(HotkeyKey.F5, HotkeyModifiers.Alt);

                var first = await service.RegisterAsync(DefaultGesture, CancellationToken.None);
                Assert.Equal(HotkeyRegistrationStatus.Registered, first.Status);

                var duplicate = await service.RegisterAsync(DefaultGesture, CancellationToken.None);
                Assert.Equal(HotkeyRegistrationStatus.AlreadyRegistered, duplicate.Status);

                var denied = await service.RegisterAsync(permission, CancellationToken.None);
                Assert.Equal(HotkeyRegistrationStatus.PermissionDenied, denied.Status);

                var unsupportedResult = await service.RegisterAsync(unsupported, CancellationToken.None);
                Assert.Equal(HotkeyRegistrationStatus.Unsupported, unsupportedResult.Status);
            }

            /// <summary>
            /// UnregisterAsync is idempotent and re-registration succeeds after
            /// an unregister.
            /// </summary>
            [Fact]
            public async Task Unregister_IsIdempotent_AllowsReregistration()
            {
                var service = new FakeGlobalHotkeyService();
                service.SetCapability(DefaultGesture, HotkeyCapability.Available);

                await service.RegisterAsync(DefaultGesture, CancellationToken.None);
                await service.UnregisterAsync(DefaultGesture, CancellationToken.None);
                await service.UnregisterAsync(DefaultGesture, CancellationToken.None);

                var again = await service.RegisterAsync(DefaultGesture, CancellationToken.None);
                Assert.Equal(HotkeyRegistrationStatus.Registered, again.Status);
            }

            /// <summary>
            /// The Pressed event fires with the gesture and event type.
            /// </summary>
            [Fact]
            public void PressedEvent_RaisesWithGestureAndType()
            {
                var service = new FakeGlobalHotkeyService();
                var received = new List<HotkeyEventArgs>();
                service.HotkeyPressed += (_, args) => received.Add(args);

                service.SimulatePress(DefaultGesture);

                var args = Assert.Single(received);
                Assert.Equal(DefaultGesture, args.Gesture);
                Assert.Equal(HotkeyEventType.Pressed, args.EventType);
            }

            /// <summary>
            /// Dispose is idempotent and detaches the event.
            /// </summary>
            [Fact]
            public void Dispose_IsIdempotent_DetachesEvent()
            {
                var service = new FakeGlobalHotkeyService();
                service.Dispose();
                service.Dispose();

                service.SimulatePress(DefaultGesture);
            }
        }

        // ------------------------------------------------------------------
        // Native library probe contract.
        // ------------------------------------------------------------------

        public sealed class NativeProbeContractTests
        {
            private static readonly string[] RequiredExports = { "MBEEncoder_Create", "MBEDecoder_Create" };

            /// <summary>
            /// A successful probe reports the logical name and no diagnostic;
            /// the result factories round-trip their inputs.
            /// </summary>
            [Fact]
            public void Probe_Success_AndResultFactories()
            {
                var probe = new FakeNativeLibraryProbe();
                probe.AddExport("MBEEncoder_Create");
                probe.AddExport("MBEDecoder_Create");

                var result = probe.Probe("dvmvocoder", RequiredExports);
                Assert.True(result.IsSuccess);
                Assert.Equal("dvmvocoder", result.LogicalName);
                Assert.Null(result.Diagnostic);

                var success = NativeLibraryProbeResult.Success("libdvmvocoder");
                Assert.True(success.IsSuccess);
                Assert.Equal("libdvmvocoder", success.LogicalName);
                Assert.Null(success.Diagnostic);

                var failure = NativeLibraryProbeResult.Failure("libdvmvocoder", "not found");
                Assert.False(failure.IsSuccess);
                Assert.Equal("not found", failure.Diagnostic);
            }

            /// <summary>
            /// A probe with missing exports fails with a diagnostic naming them.
            /// </summary>
            [Fact]
            public void Probe_Failure_ReportsMissingExports()
            {
                var probe = new FakeNativeLibraryProbe();
                probe.AddExport("MBEEncoder_Create");

                var result = probe.Probe("dvmvocoder", RequiredExports);
                Assert.False(result.IsSuccess);
                Assert.NotNull(result.Diagnostic);
                Assert.Contains("MBEDecoder_Create", result.Diagnostic);
            }

            /// <summary>
            /// The probe validates its arguments.
            /// </summary>
            [Fact]
            public void Probe_NullOrEmptyExports_Throw()
            {
                var probe = new FakeNativeLibraryProbe();

                Assert.Throws<ArgumentNullException>(() => probe.Probe("dvmvocoder", null!));
                Assert.Throws<ArgumentException>(() => probe.Probe("dvmvocoder", Array.Empty<string>()));
                Assert.Throws<ArgumentException>(() => probe.Probe("   ", RequiredExports));
            }

            /// <summary>
            /// Probe takes a logical library name, never an OS file name with a
            /// hard-coded extension.
            /// </summary>
            [Fact]
            public void Probe_RejectsFileNameStyleLogicalName()
            {
                var probe = new FakeNativeLibraryProbe();

                Assert.Throws<ArgumentException>(() => probe.Probe("dvmvocoder.dll", RequiredExports));
                Assert.Throws<ArgumentException>(() => probe.Probe("libdvmvocoder.so", RequiredExports));
                Assert.Throws<ArgumentException>(() => probe.Probe("libdvmvocoder.dylib", RequiredExports));

                var result = probe.Probe("dvmvocoder", RequiredExports);
                Assert.False(result.IsSuccess);
            }
        }

        // ------------------------------------------------------------------
        // PlatformServices composition contract.
        // ------------------------------------------------------------------

        public sealed class PlatformServicesContractTests
        {
            private static (FakeAudioStreamFactory Factory, FakeDeviceCatalog Catalog, FakeFileDialogService Dialogs, FakeGlobalHotkeyService Hotkeys, FakeNativeLibraryProbe Probe) CreateFakes()
            {
                var catalog = new FakeDeviceCatalog(
                    new[] { new AudioDeviceInfo(AudioDeviceId.Default, AudioDeviceDirection.Input, "Default input") },
                    new[] { new AudioDeviceInfo(AudioDeviceId.Default, AudioDeviceDirection.Output, "Default output") });
                return (
                    new FakeAudioStreamFactory(),
                    catalog,
                    new FakeFileDialogService(),
                    new FakeGlobalHotkeyService(),
                    new FakeNativeLibraryProbe());
            }

            /// <summary>
            /// The constructor injects every service and exposes them as
            /// read-only properties, together with stable identity values.
            /// </summary>
            [Fact]
            public void Ctor_InjectsServices_AndExposesIdentity()
            {
                var (factory, catalog, dialogs, hotkeys, probe) = CreateFakes();
                var services = new PlatformServices(factory, catalog, dialogs, hotkeys, probe);

                Assert.Same(factory, services.AudioStreams);
                Assert.Same(catalog, services.Devices);
                Assert.Same(dialogs, services.Dialogs);
                Assert.Same(hotkeys, services.Hotkeys);
                Assert.Same(probe, services.NativeProbe);
                Assert.Equal("DvmConsole.Platform", services.Name);
                Assert.False(string.IsNullOrWhiteSpace(services.Version));
            }

            /// <summary>
            /// Every constructor parameter rejects null with its own parameter name.
            /// </summary>
            [Fact]
            public void Ctor_NullParam_ThrowsWithParamName()
            {
                var (factory, catalog, dialogs, hotkeys, probe) = CreateFakes();

                var audioStreams = Assert.Throws<ArgumentNullException>(
                    () => new PlatformServices(null!, catalog, dialogs, hotkeys, probe));
                Assert.Equal("audioStreams", audioStreams.ParamName);

                var devices = Assert.Throws<ArgumentNullException>(
                    () => new PlatformServices(factory, null!, dialogs, hotkeys, probe));
                Assert.Equal("devices", devices.ParamName);

                var dialogServices = Assert.Throws<ArgumentNullException>(
                    () => new PlatformServices(factory, catalog, null!, hotkeys, probe));
                Assert.Equal("dialogs", dialogServices.ParamName);

                var hotkeyServices = Assert.Throws<ArgumentNullException>(
                    () => new PlatformServices(factory, catalog, dialogs, null!, probe));
                Assert.Equal("hotkeys", hotkeyServices.ParamName);

                var nativeProbe = Assert.Throws<ArgumentNullException>(
                    () => new PlatformServices(factory, catalog, dialogs, hotkeys, null!));
                Assert.Equal("nativeProbe", nativeProbe.ParamName);
            }

            /// <summary>
            /// DisposeAsync propagates disposal to every injected service
            /// (async for IAsyncDisposable, sync for IDisposable) and is idempotent.
            /// </summary>
            [Fact]
            public async Task DisposeAsync_DisposesAllServices_AndIsIdempotent()
            {
                var (factory, catalog, dialogs, hotkeys, probe) = CreateFakes();
                var services = new PlatformServices(factory, catalog, dialogs, hotkeys, probe);

                await services.DisposeAsync();
                await services.DisposeAsync();

                Assert.True(factory.DisposedAsync);
                Assert.True(catalog.DisposedAsync);
                Assert.True(dialogs.DisposedAsync);
                Assert.True(hotkeys.Disposed);
                Assert.True(probe.DisposedAsync);
            }

            /// <summary>
            /// The platform assembly references framework assemblies plus the
            /// selected managed MP3 decoder dependency used by Gate 6.2.
            /// </summary>
            [Fact]
            public void Assembly_ReferencesOnlyFrameworkAssemblies()
            {
                var assembly = typeof(PlatformServices).Assembly;

                Assert.Same(typeof(PlatformInfo).Assembly, assembly);

                Assert.All(
                    assembly.GetReferencedAssemblies(),
                    reference =>
                    {
                        var name = reference.Name ?? string.Empty;
                        Assert.True(
                            name == "NLayer"
                                || name == "mscorlib"
                                || name == "netstandard"
                                || name.StartsWith("System", StringComparison.Ordinal),
                            $"Unexpected non-framework assembly reference: {name}");
                    });
            }
        }
    }
}
