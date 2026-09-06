// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.ExceptionServices;

namespace DvmConsole.Media;

// Serializes one protocol stream's control and media packets at its native
// packet cadence. The first packet is sent immediately; every later packet is
// held until the next packet opportunity, including startup metadata and
// padded call completion.
internal sealed class ProtocolPacketPacer<TPacket> : IDisposable
{
    private const int DefaultCapacity = 64;
    private readonly object sync = new();
    private readonly Queue<TPacket> packets = new(DefaultCapacity);
    private readonly Action<TPacket> send;
    private readonly Func<CancellationToken, ValueTask> waitForNextPacket;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool accepting = true;
    private bool pumping;
    private bool disposed;
    private Exception? failure;

    public ProtocolPacketPacer(
        TimeSpan packetInterval,
        Action<TPacket> send,
        TimeProvider? timeProvider = null)
        : this(
            new TransmitFrameCadence(packetInterval, timeProvider).WaitForNextFrameAsync,
            send)
    {
    }

    internal ProtocolPacketPacer(
        Func<CancellationToken, ValueTask> waitForNextPacket,
        Action<TPacket> send)
    {
        this.waitForNextPacket = waitForNextPacket ??
            throw new ArgumentNullException(nameof(waitForNextPacket));
        this.send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public void Enqueue(TPacket packet)
    {
        bool startPump;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failure is not null)
                throw new InvalidOperationException("The protocol packet pacer has faulted.", failure);
            if (!accepting)
                throw new InvalidOperationException("The protocol packet pacer is completing.");
            if (packets.Count >= DefaultCapacity)
                throw new InvalidOperationException(
                    $"The protocol packet backlog reached its {DefaultCapacity}-packet safety limit.");

            packets.Enqueue(packet);
            startPump = !pumping;
            pumping = true;
        }

        if (!startPump)
            return;

        TaskObservation.Observe(PumpAsync());
        Exception? synchronousFailure;
        lock (sync)
            synchronousFailure = failure;
        if (synchronousFailure is not null)
            ExceptionDispatchInfo.Capture(synchronousFailure).Throw();
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        bool completeNow;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            accepting = false;
            completeNow = !pumping && packets.Count == 0;
        }
        if (completeNow)
            completion.TrySetResult();

        try
        {
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellation.Cancel();
            throw;
        }
    }

    public void Dispose()
    {
        bool cancelNow;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            accepting = false;
            packets.Clear();
            cancelNow = !pumping;
        }

        cancellation.Cancel();
        if (cancelNow)
            completion.TrySetCanceled(cancellation.Token);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                TPacket packet;
                lock (sync)
                {
                    if (packets.Count == 0)
                    {
                        pumping = false;
                        if (!accepting)
                            completion.TrySetResult();
                        return;
                    }

                    packet = packets.Dequeue();
                }

                await waitForNextPacket(cancellation.Token).ConfigureAwait(false);
                send(packet);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (sync)
            {
                packets.Clear();
                pumping = false;
            }
            completion.TrySetCanceled(cancellation.Token);
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                failure = exception;
                accepting = false;
                packets.Clear();
                pumping = false;
            }
            completion.TrySetException(exception);
        }
    }
}
