using System.Diagnostics;

namespace DvmConsole.Audio;

/// <summary>
/// Adapts an explicitly configured FFmpeg executable to the streaming PCM
/// reader contract. FFmpeg receives the original source on stdin and emits
/// signed little-endian mono PCM at the console voice rate on stdout.
/// </summary>
public sealed class FfmpegPcmStreamReader : IAudioPcmStreamReader
{
    public const int OutputSampleRate = 8000;

    private readonly Stream source;
    private readonly Process process;
    private readonly Stream output;
    private readonly Task inputTask;
    private readonly Task<string> errorTask;
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly CancellationTokenSource processCancellation = new();
    private byte[] rawSamples = [];
    private bool disposed;

    private FfmpegPcmStreamReader(Stream source, Process process)
    {
        this.source = source;
        this.process = process;
        output = process.StandardOutput.BaseStream;
        errorTask = process.StandardError.ReadToEndAsync();
        inputTask = CopySourceAsync();
    }

    public int SampleRate => OutputSampleRate;

    public static async Task<FfmpegPcmStreamReader> OpenAsync(
        Stream source,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!source.CanRead)
            throw new ArgumentException("The audio source must be readable.", nameof(source));

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("-acodec");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add(OutputSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("pipe:1");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The FFmpeg process did not start.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            await source.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(
                $"The configured FFmpeg decoder could not be started: {exception.Message}",
                exception);
        }

        return new FfmpegPcmStreamReader(source, process);
    }

    public async ValueTask<int> ReadSamplesAsync(
        Memory<short> destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (destination.IsEmpty)
            return 0;

        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state => ((FfmpegPcmStreamReader)state!).KillProcess(),
            this);
        try
        {
            int targetBytes = checked(destination.Length * sizeof(short));
            EnsureRawBuffer(targetBytes);
            int byteCount = 0;

            while (byteCount < targetBytes)
            {
                int read = await output.ReadAsync(
                    rawSamples.AsMemory(byteCount, targetBytes - byteCount),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    await EnsureProcessCompletedAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                byteCount += read;
            }

            if ((byteCount & 1) != 0)
                throw new InvalidDataException("FFmpeg returned an incomplete PCM sample.");

            int sampleCount = byteCount / sizeof(short);
            for (int index = 0; index < sampleCount; index++)
            {
                destination.Span[index] = (short)(rawSamples[index * 2] | (rawSamples[index * 2 + 1] << 8));
            }

            return sampleCount;
        }
        finally
        {
            readGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        KillProcess();

        try
        {
            await inputTask.ConfigureAwait(false);
        }
        catch
        {
            // Process shutdown can interrupt a source copy; disposal must remain idempotent.
        }

        try
        {
            await errorTask.ConfigureAwait(false);
        }
        catch
        {
            // The process is already being torn down.
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposal should not mask the original playback failure.
        }

        await source.DisposeAsync().ConfigureAwait(false);
        process.Dispose();
        processCancellation.Dispose();
        readGate.Dispose();
    }

    private async Task CopySourceAsync()
    {
        try
        {
            await using Stream input = process.StandardInput.BaseStream;
            await source.CopyToAsync(input, processCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception) when (disposed || process.HasExited)
        {
            // A stopped or failed decoder closes stdin while the source is still copying.
        }
    }

    private async Task EnsureProcessCompletedAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        processCancellation.Cancel();
        await inputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? "no diagnostic was returned" : error.Trim();
            throw new InvalidDataException($"FFmpeg could not decode the audio stream: {detail}");
        }
    }

    private void EnsureRawBuffer(int byteCount)
    {
        if (rawSamples.Length < byteCount)
            rawSamples = new byte[byteCount];
    }

    private void KillProcess()
    {
        processCancellation.Cancel();
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process may have exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process may already be terminating during cancellation.
        }
    }
}
