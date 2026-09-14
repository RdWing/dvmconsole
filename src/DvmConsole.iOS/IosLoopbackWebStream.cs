// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Text;
using DvmConsole.Application;

namespace DvmConsole.iOS;

/// <summary>Bounded HTTP/PCM fixture used only by explicit simulator qualification.</summary>
internal sealed class IosLoopbackWebStream : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private int requests;
    private readonly int durationSeconds;
    public string Url { get; }

    public IosLoopbackWebStream(int durationSeconds = 20)
    {
        this.durationSeconds = Math.Clamp(durationSeconds, 20, 360);
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/qualification.wav";
        worker = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        byte[] wave = CreateWave(durationSeconds);
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: {wave.Length}\r\nConnection: close\r\n\r\n");
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                using var connection = client.GetStream();
                try
                {
                    // Limit header storage even though this server only accepts our own client.
                    byte[] header = new byte[4096];
                    int count = 0;
                    while (count < header.Length)
                    {
                        int read = await connection.ReadAsync(header.AsMemory(count), lifetime.Token);
                        if (read == 0) break;
                        count += read;
                        if (Encoding.ASCII.GetString(header, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    Interlocked.Increment(ref requests);
                    await connection.WriteAsync(response, lifetime.Token);
                    await connection.WriteAsync(wave, lifetime.Token);
                }
                catch (IOException) { /* Stopping playback closes the fixture connection. */ }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }

    public async Task VerifyAsync(ConsoleReceiveSession session, IosListeningControls listening)
    {
        var stream = session.WebStreams.Single();
        await session.SetWebStreamPlayingAsync(stream.Id, true);
        if (!session.WebStreams.Single().Playback.IsActive || Volatile.Read(ref requests) != 1)
            throw new InvalidOperationException("Loopback web playback failed: " + session.WebStreams.Single().Playback.Status);
        await session.SetWebStreamVolumeAsync(stream.Id, 0.5);
        var channel = session.CaptureTopology().Channels.Single().Id;
        await session.SetReceiveEnabledAsync(channel, false);
        if (listening.Pause() != MediaPlayer.MPRemoteCommandHandlerStatus.Success || !await listening.ResumeAsync(session))
            throw new InvalidOperationException("System playback controls failed with only a web stream selected.");
        if (!session.WebStreams.Single().Playback.IsActive || Volatile.Read(ref requests) != 2)
            throw new InvalidOperationException("Web-stream recovery did not open exactly one fresh source.");
        await session.SetWebStreamPlayingAsync(stream.Id, false);
        if (session.WebStreams.Single() is { Selected: true } or { Playback.IsActive: true })
            throw new InvalidOperationException("Manual web-stream stop retained playback intent.");
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (MediaPlayer.MPRemoteCommandCenter.Shared.PlayCommand.Enabled != session.ConnectionStates.Any(state => state.State == RadioConnectionState.Connected))
                throw new InvalidOperationException("System playback controls retained an empty listening selection.");
        });
        await session.SetReceiveEnabledAsync(channel, true);
    }

    private static byte[] CreateWave(int durationSeconds)
    {
        int samples = 8000 * durationSeconds;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (int i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(2 * Math.PI * 440 * i / 8000)));
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop();
        try { await worker; }
        finally { lifetime.Dispose(); }
    }
}
