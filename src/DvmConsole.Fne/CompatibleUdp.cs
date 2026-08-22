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

// FnePeer constructs its traffic receiver followed by its metadata receiver.
// This short-lived ambient scope lets each pinned peer capture an independent
// transport mode and channel role without maintaining a private fnecore fork.
public static class FneTransportEncryptionContext
{
    private static readonly AsyncLocal<ContextState?> Current = new();

    internal static (
        FneTransportEncryptionMode Mode,
        FneUdpChannelKind ChannelKind,
        Action<long>? TrafficIngressObserver) Capture()
    {
        ContextState? state = Current.Value;
        if (state is null)
            return (FneTransportEncryptionMode.Auto, FneUdpChannelKind.Traffic, null);

        FneUdpChannelKind channelKind = state.ReceiverCount++ == 1
            ? FneUdpChannelKind.Metadata
            : FneUdpChannelKind.Traffic;
        return (state.Mode, channelKind, state.TrafficIngressObserver);
    }

    public static IDisposable Use(FneTransportEncryptionMode mode)
        => Use(mode, trafficIngressObserver: null);

    public static IDisposable Use(
        FneTransportEncryptionMode mode,
        Action<long>? trafficIngressObserver)
    {
        ContextState? previous = Current.Value;
        Current.Value = new ContextState(mode, trafficIngressObserver);
        return new Scope(previous);
    }

    private sealed class ContextState(
        FneTransportEncryptionMode mode,
        Action<long>? trafficIngressObserver)
    {
        public FneTransportEncryptionMode Mode { get; } = mode;
        public Action<long>? TrafficIngressObserver { get; } = trafficIngressObserver;
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

public struct UdpFrame
{
    public IPEndPoint Endpoint;
    public byte[] Message;
}

public abstract class UdpBase
{
    protected readonly UdpClient client;

    private readonly FneUdpChannelKind channelKind;
    private readonly Action<long>? trafficIngressObserver;
    private readonly FneTransportNegotiationState encryptionState;
    private readonly InboundReplayWindow replayWindow = new();

    protected UdpBase()
    {
        client = new UdpClient();
        var context = FneTransportEncryptionContext.Capture();
        channelKind = context.ChannelKind;
        trafficIngressObserver = context.TrafficIngressObserver;
        encryptionState = new FneTransportNegotiationState(context.Mode);
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
            UdpReceiveResult result = await client.ReceiveAsync().ConfigureAwait(false);
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

            return new UdpFrame
            {
                Message = message,
                Endpoint = result.RemoteEndPoint
            };
        }
    }

    protected byte[] WrapForSend(byte[] message)
        => encryptionState.WrapForSend(message);
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
        endpoint = destination;
        client.Connect(destination.Address.ToString(), destination.Port);
        connected = true;
    }

    public void Send(UdpFrame frame)
    {
        frame.Message = WrapForSend(frame.Message);
        if (connected)
            client.Send(frame.Message, frame.Message.Length);
        else
            client.Send(frame.Message, frame.Message.Length, frame.Endpoint);
    }
}
