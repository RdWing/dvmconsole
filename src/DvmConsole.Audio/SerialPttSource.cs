using System.IO.Ports;
using System.Text;

namespace DvmConsole.Audio;

/// <summary>
/// Lifecycle-bound hardware PTT adapter for USB serial footswitches and small
/// serial controllers. The device sends one state token per line: on/1/true/
/// pressed assert PTT, and off/0/false/released release it. Unknown lines are
/// ignored and the source always returns to released on EOF, stop, or fault.
/// </summary>
public sealed class SerialPttSource : IPttSource
{
    private readonly Func<Stream> openStream;
    private readonly object sync = new();
    private Stream? stream;
    private CancellationTokenSource? cancellation;
    private Task? readTask;
    private bool started;
    private bool disposed;

    public SerialPttSource(string portName, int baudRate = 9_600)
        : this(() => OpenSerialStream(portName, baudRate))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(baudRate));

        PortName = portName;
        BaudRate = baudRate;
    }

    /// <summary>
    /// Creates a source from an already-open input stream. This overload also
    /// supports host-specific serial transports and deterministic tests.
    /// </summary>
    public SerialPttSource(Func<Stream> openStream)
    {
        this.openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
    }

    public event EventHandler<bool>? StateChanged;

    public bool IsPressed { get; private set; }

    public string? PortName { get; }

    public int? BaudRate { get; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            if (started)
                return ValueTask.CompletedTask;
        }

        Stream created = openStream();
        var createdCancellation = new CancellationTokenSource();
        lock (sync)
        {
            if (disposed)
            {
                createdCancellation.Dispose();
                created.Dispose();
                throw new ObjectDisposedException(nameof(SerialPttSource));
            }

            if (started)
            {
                createdCancellation.Dispose();
                created.Dispose();
                return ValueTask.CompletedTask;
            }

            stream = created;
            cancellation = createdCancellation;
            started = true;
            readTask = ReadLoopAsync(created, createdCancellation.Token);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Stream? oldStream;
        CancellationTokenSource? oldCancellation;
        Task? oldReadTask;
        lock (sync)
        {
            if (!started)
            {
                SetPressed(false);
                return;
            }

            started = false;
            oldStream = stream;
            oldCancellation = cancellation;
            oldReadTask = readTask;
            stream = null;
            cancellation = null;
            readTask = null;
        }

        oldCancellation?.Cancel();
        if (oldStream is not null)
            await oldStream.DisposeAsync().ConfigureAwait(false);
        if (oldReadTask is not null)
        {
            try
            {
                await oldReadTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (oldCancellation?.IsCancellationRequested == true)
            {
                // Expected when the operator stops the device.
            }
        }

        oldCancellation?.Dispose();
        SetPressed(false);
    }

    public async ValueTask DisposeAsync()
    {
        bool shouldStop;
        lock (sync)
        {
            shouldStop = !disposed;
            disposed = true;
        }

        if (shouldStop)
            await StopAsync().ConfigureAwait(false);
    }

    public static bool TryParseState(string? line, out bool pressed)
    {
        string value = line?.Trim() ?? string.Empty;
        int separator = value.IndexOf('=');
        if (separator >= 0)
        {
            if (!value[..separator].Trim().Equals("ptt", StringComparison.OrdinalIgnoreCase))
            {
                pressed = false;
                return false;
            }

            value = value[(separator + 1)..].Trim();
        }

        switch (value.ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "press":
            case "pressed":
                pressed = true;
                return true;
            case "0":
            case "false":
            case "off":
            case "release":
            case "released":
                pressed = false;
                return true;
            default:
                pressed = false;
                return false;
        }
    }

    private async Task ReadLoopAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(
                source,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 256,
                leaveOpen: true);

            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                if (TryParseState(line, out bool pressed))
                    SetPressed(pressed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the operator stops the source.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposing the serial stream is how StopAsync interrupts a read.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A disconnected or faulted hardware source fails safe to released.
        }
        finally
        {
            SetPressed(false);
        }
    }

    private void SetPressed(bool pressed)
    {
        EventHandler<bool>? handler;
        lock (sync)
        {
            if (IsPressed == pressed)
                return;

            IsPressed = pressed;
            handler = StateChanged;
        }

        handler?.Invoke(this, pressed);
    }

    private static Stream OpenSerialStream(string portName, int baudRate)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            NewLine = "\n",
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout
        };

        try
        {
            port.Open();
            return new OwnedSerialStream(port.BaseStream, port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private sealed class OwnedSerialStream(Stream input, SerialPort owner) : Stream
    {
        private readonly Stream input = input;
        private readonly SerialPort owner = owner;

        public override bool CanRead => input.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => input.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => input.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => input.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => input.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    input.Dispose();
                }
                finally
                {
                    owner.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
