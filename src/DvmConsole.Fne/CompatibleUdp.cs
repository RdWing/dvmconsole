#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Fixed Network Equipment Core Library
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Fixed Network Equipment Core Library
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2022,2024 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2026 RdWing
*
*/

using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace fnecore;

public enum FneTransportEncryptionMode
{
    Auto,
    Ecb,
    Cbc
}

internal enum FneUdpChannelKind
{
    Traffic,
    Metadata
}

// Owns the UDP receivers created for one FnePeer. The pinned peer's Stop()
// cancels its own loop tokens but does not close these sockets, so the
// compatibility transport releases them as soon as the closing packet has
// been attempted. Dispose remains the idempotent fallback for partial starts.
internal sealed class FneTransportLifetime : IDisposable
{
    private readonly object sync = new();
    private readonly List<Action> stopActions = [];
    private bool stopping;
    private bool stopped;

    public bool IsStopping
    {
        get
        {
            lock (sync)
                return stopping;
        }
    }

    public bool IsStopped
    {
        get
        {
            lock (sync)
                return stopped;
        }
    }

    public void Register(Action stop)
    {
        ArgumentNullException.ThrowIfNull(stop);

        bool stopImmediately;
        lock (sync)
        {
            stopImmediately = stopping;
            if (!stopImmediately)
                stopActions.Add(stop);
        }

        if (stopImmediately)
            StopSafely(stop);
    }

    public void BeginStop()
    {
        lock (sync)
            stopping = true;
    }

    public void Dispose()
    {
        Action[] pending;
        lock (sync)
        {
            if (stopped)
                return;

            stopping = true;
            stopped = true;
            pending = stopActions.ToArray();
            stopActions.Clear();
        }

        foreach (Action stop in pending)
            StopSafely(stop);
    }

    private static void StopSafely(Action stop)
    {
        try
        {
            stop();
        }
        catch (ObjectDisposedException)
        {
            // Receiver shutdown is idempotent.
        }
    }
}

internal sealed record FneTransportObservers(
    Action<long>? TrafficIngress,
    Action<FneTalkgroupAnnouncement>? TalkgroupAnnouncement,
    Action? LoginRequestSent = null);

internal readonly record struct FneTransportSession(
    FneTransportEncryptionMode EncryptionMode,
    FneUdpChannelKind ChannelKind,
    FneTransportObservers Observers,
    FneTransportLifetime? Lifetime);

// FnePeer constructs its traffic receiver followed by its metadata receiver.
// This short-lived ambient scope supplies the complete application-owned
// transport session without adding responsibilities to the encryption facade.
internal static class FneTransportSessionContext
{
    private static readonly AsyncLocal<ContextState?> Current = new();
    private static readonly FneTransportObservers EmptyObservers = new(null, null, null);

    public static FneTransportSession Capture()
    {
        ContextState? state = Current.Value;
        if (state is null)
        {
            return new FneTransportSession(
                FneTransportEncryptionMode.Auto,
                FneUdpChannelKind.Traffic,
                EmptyObservers,
                null);
        }

        FneUdpChannelKind channelKind = state.ReceiverCount++ == 1
            ? FneUdpChannelKind.Metadata
            : FneUdpChannelKind.Traffic;
        return new FneTransportSession(
            state.EncryptionMode,
            channelKind,
            state.Observers,
            state.Lifetime);
    }

    public static IDisposable Use(
        FneTransportEncryptionMode encryptionMode,
        FneTransportObservers observers,
        FneTransportLifetime? lifetime)
    {
        ArgumentNullException.ThrowIfNull(observers);
        ContextState? previous = Current.Value;
        Current.Value = new ContextState(
            encryptionMode,
            observers,
            lifetime);
        return new Scope(previous);
    }

    private sealed class ContextState(
        FneTransportEncryptionMode encryptionMode,
        FneTransportObservers observers,
        FneTransportLifetime? lifetime)
    {
        public FneTransportEncryptionMode EncryptionMode { get; } = encryptionMode;
        public FneTransportObservers Observers { get; } = observers;
        public FneTransportLifetime? Lifetime { get; } = lifetime;
        public int ReceiverCount { get; set; }
    }

    private sealed class Scope(ContextState? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;

            Current.Value = previous;
            disposed = true;
        }
    }
}

// Compatibility facade for consumers that only select transport encryption.
// Application session composition belongs to FneTransportSessionContext.
public static class FneTransportEncryptionContext
{
    public static IDisposable Use(FneTransportEncryptionMode mode)
        => Use(mode, trafficIngressObserver: null);

    public static IDisposable Use(
        FneTransportEncryptionMode mode,
        Action<long>? trafficIngressObserver)
        => FneTransportSessionContext.Use(
            mode,
            new FneTransportObservers(trafficIngressObserver, null),
            lifetime: null);
}

public struct UdpFrame
{
    public IPEndPoint Endpoint;
    public byte[] Message;
}

public abstract class UdpBase
{
    protected readonly UdpClient client;

    private readonly CancellationTokenSource receiveCancellation = new();
    private readonly FneTransportLifetime? transportLifetime;
    private readonly FneUdpChannelKind channelKind;
    private readonly Action<long>? trafficIngressObserver;
    private readonly Action<FneTalkgroupAnnouncement>? talkgroupAnnouncementObserver;
    private readonly Action? loginRequestSentObserver;
    private readonly FneTransportNegotiationState encryptionState;
    private readonly InboundReplayWindow replayWindow = new();
    private int stopped;

    protected UdpBase()
    {
        client = new UdpClient();
        FneTransportSession context = FneTransportSessionContext.Capture();
        channelKind = context.ChannelKind;
        trafficIngressObserver = context.Observers.TrafficIngress;
        talkgroupAnnouncementObserver = context.Observers.TalkgroupAnnouncement;
        loginRequestSentObserver = context.Observers.LoginRequestSent;
        transportLifetime = context.Lifetime;
        encryptionState = new FneTransportNegotiationState(context.EncryptionMode);
        context.Lifetime?.Register(Stop);
    }

    public FneTransportEncryptionMode ConfiguredEncryptionMode => encryptionState.ConfiguredMode;

    public FneTransportEncryptionMode? NegotiatedEncryptionMode => encryptionState.NegotiatedMode;

    public void SetPresharedKey(byte[]? key)
    {
        encryptionState.SetPresharedKey(key);
        replayWindow.Clear();
    }

    public async Task<UdpFrame> Receive()
    {
        while (true)
        {
            if (IsStopped)
                return StoppedFrame();

            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(receiveCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (IsStopped)
            {
                return StoppedFrame();
            }
            catch (ObjectDisposedException) when (IsStopped)
            {
                return StoppedFrame();
            }
            catch (SocketException) when (IsStopped)
            {
                return StoppedFrame();
            }
            if (channelKind == FneUdpChannelKind.Traffic && trafficIngressObserver is not null)
            {
                try
                {
                    trafficIngressObserver(Stopwatch.GetTimestamp());
                }
                catch
                {
                    // Timing observation is diagnostic only and must not stop
                    // the protocol receiver.
                }
            }
            if (!FneInboundFramePolicy.AcceptsInbound(channelKind))
                continue;

            byte[] message = encryptionState.Unwrap(result.Buffer, out bool wrapped);

            if (!FneInboundFramePolicy.ShouldDeliverTraffic(message))
                continue;
            if (wrapped && !replayWindow.TryRemember(result.Buffer))
                continue;
            if (talkgroupAnnouncementObserver is not null &&
                FneInboundFramePolicy.TryParseValidatedTalkgroupAnnouncement(
                    message,
                    out FneTalkgroupAnnouncement? announcement))
            {
                try
                {
                    talkgroupAnnouncementObserver(announcement!);
                }
                catch
                {
                    // Authority observation must not interrupt the protocol receiver.
                }
            }

            return new UdpFrame
            {
                Message = message,
                Endpoint = result.RemoteEndPoint
            };
        }
    }

    protected byte[] WrapForSend(byte[] message)
        => encryptionState.WrapForSend(message);

    protected void ObserveOutboundFrame(ReadOnlySpan<byte> message)
    {
        if (channelKind != FneUdpChannelKind.Traffic ||
            loginRequestSentObserver is null ||
            !message.StartsWith("RPTL"u8))
        {
            return;
        }

        try
        {
            loginRequestSentObserver();
        }
        catch
        {
            // Retry observation must never interrupt protocol transmission.
        }
    }

    protected bool IsStopped => Volatile.Read(ref stopped) != 0;

    protected bool IsStopping => transportLifetime?.IsStopping == true;

    protected void CompleteTransportStop() => transportLifetime?.Dispose();

    private void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;

        receiveCancellation.Cancel();
        client.Dispose();
        receiveCancellation.Dispose();
    }

    private static UdpFrame StoppedFrame()
        => new()
        {
            Endpoint = new IPEndPoint(IPAddress.None, 0),
            Message = []
        };
}

public sealed class UdpReceiver : UdpBase
{
    private IPEndPoint? endpoint;
    private bool connected;

    public IPEndPoint? EndPoint => endpoint;

    public void Connect(string hostName, int port)
    {
        try
        {
            if (!IPAddress.TryParse(hostName, out IPAddress? address))
                address = Dns.GetHostAddresses(hostName).FirstOrDefault();
            if (address is null)
                return;

            Connect(new IPEndPoint(address, port));
        }
        catch (SocketException)
        {
            return;
        }
    }

    public void Connect(IPEndPoint destination)
    {
        if (IsStopped || IsStopping)
            return;

        endpoint = destination;
        try
        {
            client.Connect(destination.Address.ToString(), destination.Port);
            connected = true;
        }
        catch (ObjectDisposedException) when (IsStopped)
        {
            // A maintenance reconnect raced with session shutdown.
        }
        catch (SocketException) when (IsStopping)
        {
            // A maintenance reconnect raced with session shutdown.
        }
    }

    public void Send(UdpFrame frame)
    {
        if (IsStopped)
            return;

        bool completesTransportStop =
            IsStopping && FneClosingFramePolicy.IsRepeaterClosing(frame.Message);
        ObserveOutboundFrame(frame.Message);
        frame.Message = WrapForSend(frame.Message);
        try
        {
            if (connected)
                client.Send(frame.Message, frame.Message.Length);
            else
                client.Send(frame.Message, frame.Message.Length, frame.Endpoint);
        }
        catch (ObjectDisposedException) when (IsStopped)
        {
            // A maintenance send raced with session shutdown.
        }
        catch (SocketException) when (IsStopping)
        {
            // Let peer shutdown continue even when its best-effort close
            // packet cannot be delivered to an already-lost endpoint.
        }
        finally
        {
            if (completesTransportStop)
                CompleteTransportStop();
        }
    }
}

internal static class FneClosingFramePolicy
{
    // The FNE extension begins after the 12-byte RTP header. Its function and
    // subfunction occupy extension bytes 6 and 7 respectively.
    private const int FunctionOffset = 18;
    private const int SubFunctionOffset = 19;

    public static bool IsRepeaterClosing(ReadOnlySpan<byte> message)
        => message.Length > SubFunctionOffset &&
           message[FunctionOffset] == Constants.NET_FUNC_RPT_CLOSING &&
           message[SubFunctionOffset] == Constants.NET_SUBFUNC_NOP;
}
