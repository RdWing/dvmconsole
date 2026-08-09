// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Networking;
using fnecore;
using fnecore.DMR;
using fnecore.NXDN;
using fnecore.P25;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// The real <see cref="IFneTransport"/>: a backgrounded adapter over
    /// <c>fnecore.FnePeer</c> (WPF PeerSystem parity). Connect,
    /// Disconnect and Dispose are deferred through an injectable
    /// <c>Action&lt;Action&gt;</c> seam (default <see cref="Task.Run"/>)
    /// because <see cref="FnePeer.Stop"/> blocks on a dead network.
    /// <see cref="FnePeer.StartWithoutMaintainence"/> is used — the Core
    /// <see cref="FneConnectionService"/> owns the heartbeat through
    /// <see cref="Ping"/>; incoming PONG frames are translated to
    /// <see cref="PingAcknowledged"/> by reading the validated RtpFNE
    /// function byte at absolute offset 18 of each raw frame in the
    /// peer's <see cref="FnePeer.NetworkFrameHandler"/> (no fnecore fork
    /// change). Receive events are re-raised as
    /// <see cref="DmrFrameReceived"/> / <see cref="P25FrameReceived"/>
    /// for the shell glue; this adapter never routes audio itself.
    /// </summary>
    public sealed class FnecorePeerAdapter : FneSystemBase, IFneTransport
    {
        /// <summary>
        /// The configured codeplug system name, mirroring WPF
        /// <c>PeerSystem.ConfiguredSystemName</c>. The fnecore peer's
        /// own <see cref="FneSystemBase.SystemName"/> is the DVMCONSOLE
        /// client identity, not the configured name.
        /// </summary>
        public string ConfiguredSystemName { get; }

        /// <summary>
        /// The fnecore peer's software identity, WPF parity
        /// (dvmconsole/PeerSystem.cs:77-80): "CONSOLE_RxxAyy" derived
        /// from the assembly version instead of a hardcoded string.
        /// </summary>
        public string SoftwareIdentity { get; }

        /// <summary>
        /// The shared "CONSOLE_RxxAyy" identity backing both
        /// <see cref="SoftwareIdentity"/> and the peer construction.
        /// </summary>
        private static readonly string SoftwareIdentityValue =
            "CONSOLE_" + AboutWindowViewModel.FormatReleaseVersion(
                Assembly.GetExecutingAssembly().GetName().Version);

        private readonly Action<Action> background;

        // Volatile: read from the deferred Connect background action
        // (thread-pool) while written from Dispose on the caller thread.
        private volatile bool disposed;

        /// <summary>
        /// Raised for every validated DMR receive event (voice and
        /// control alike); the shell glue classifies the frames.
        /// </summary>
        public event Action<DMRDataReceivedEvent>? DmrFrameReceived;

        /// <summary>
        /// Raised for every validated P25 receive event (voice and
        /// control alike); the shell glue classifies the frames.
        /// </summary>
        public event Action<P25DataReceivedEvent>? P25FrameReceived;

        /// <summary>
        /// Backing store for the explicitly implemented
        /// <see cref="IFneTransport.PeerConnected"/> event. The class
        /// cannot declare a public event named <c>PeerConnected</c> — the
        /// inherited fnecore abstract member
        /// <see cref="FneSystemBase.PeerConnected(object, PeerConnectedEvent)"/>
        /// owns that name — so the interface surface is provided
        /// explicitly and the override below relays into this store.
        /// </summary>
        private Action? peerConnectedHandlers;

        /// <inheritdoc />
        event Action? IFneTransport.PeerConnected
        {
            add => peerConnectedHandlers += value;
            remove => peerConnectedHandlers -= value;
        }

        /// <inheritdoc />
        public event Action? PeerDisconnected;

        /// <inheritdoc />
        public event Action? PingAcknowledged;

        /// <summary>
        /// Creates the adapter for the given system configuration.
        /// </summary>
        /// <param name="system">The configured FNE system.</param>
        /// <param name="background">The deferral seam; defaults to <see cref="Task.Run"/>.</param>
        public FnecorePeerAdapter(Codeplug.System system, Action<Action>? background = null)
            : base(CreatePeer(system, SoftwareIdentityValue))
        {
            this.background = background ?? (action => Task.Run(action));
            ConfiguredSystemName = system?.Name ?? string.Empty;
            SoftwareIdentity = SoftwareIdentityValue;

            // Relay fnecore's own connection-state events. PeerConnected
            // fires from the login ACK path; PeerDisconnected is the
            // peer's public action field raised on connection loss.
            // Subscriber invocation is isolated: fnecore's listen loop
            // must never see an exception thrown by a subscriber.
            fne.PeerDisconnected += _ =>
            {
                try
                {
                    PeerDisconnected?.Invoke();
                }
                catch
                {
                    // Subscriber exceptions are swallowed at the adapter
                    // boundary — the adapter is the isolation fence.
                }
            };

            // Translate incoming PONG frames into PingAcknowledged. The
            // handler observes every raw frame: PONGs raise the
            // acknowledged event and return false so fnecore still
            // processes the frame (updating its own ping counters); all
            // other frames are left untouched.
            //
            // PONG is detected with an allocation-free direct read:
            // RtpFNEHeader.Function sits at absolute byte offset 18
            // (RTP header 12 + extension field offset 6,
            // fnecore/RtpFNEHeader.cs:60,86). The byte is only read
            // after the length guard, so short or malformed frames are
            // safe. ReadFrame validates reachable network frames before
            // invoking this handler; the guard additionally covers
            // direct invocations.
            fne.NetworkFrameHandler = (frame, peerId, streamId) =>
            {
                if (frame.Message.Length > 18 && frame.Message[18] == Constants.NET_FUNC_PONG)
                {
                    try
                    {
                        PingAcknowledged?.Invoke();
                    }
                    catch
                    {
                        // Subscriber exceptions are swallowed at the
                        // adapter boundary — this handler runs inside
                        // fnecore's async void listen loop, which only
                        // catches InvalidOperationException/SocketException.
                    }
                }

                return false;
            };
        }

        /// <summary>
        /// Builds the <see cref="FnePeer"/> exactly like WPF
        /// PeerSystem.Create (PeerSystem.cs:52-104): address/port from
        /// the system (IP literal or DNS-resolved hostname), preshared
        /// key only when the system is encrypted, passphrase from the
        /// password, conventional-peer details with the configured
        /// identity, and PingTime=5 (the Core service drives the actual
        /// pings).
        /// </summary>
        /// <param name="system">The configured FNE system.</param>
        /// <returns>The configured, unstarted peer.</returns>
        private static FnePeer CreatePeer(Codeplug.System system, string softwareIdentity)
        {
            if (system is null)
            {
                throw new ArgumentNullException(nameof(system));
            }

            if (string.IsNullOrEmpty(system.Address))
            {
                throw new ArgumentException("address", nameof(system));
            }

            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, system.Port);
            try
            {
                endpoint = new IPEndPoint(IPAddress.Parse(system.Address), system.Port);
            }
            catch (FormatException)
            {
                IPAddress[] addresses = Dns.GetHostAddresses(system.Address);
                if (addresses.Length > 0)
                {
                    endpoint = new IPEndPoint(addresses[0], system.Port);
                }
            }

            string? key = system.Encrypted ? system.PresharedKey : null;

            FnePeer peer = new FnePeer("DVMCONSOLE", system.PeerId, endpoint, key);

            if (string.IsNullOrEmpty(system.Identity))
            {
                system.Identity = system.PeerId.ToString();
            }

            peer.Passphrase = system.Password;
            peer.Information = new PeerInformation
            {
                Details = new PeerDetails
                {
                    ConventionalPeer = true,
                    Software = softwareIdentity,
                    Identity = system.Identity,
                },
            };

            peer.PingTime = 5;

            return peer;
        }

        /// <inheritdoc />
        public void Connect()
        {
            background(() =>
            {
                // The deferred Start must not run after teardown: a peer
                // started post-Dispose would leak its listen threads and
                // the UDP socket with no heartbeat owner.
                if (disposed)
                {
                    return;
                }

                try
                {
                    // StartWithoutMaintainence: the Core service owns the
                    // heartbeat through Ping() (WPF parity).
                    if (!fne.IsStarted)
                    {
                        fne.StartWithoutMaintainence();

                        // fnecore sends NET_FUNC_RPTL only from its
                        // maintenance loop (FnePeer.cs:1508-1511), which
                        // the adapter deliberately skips — the Core
                        // service owns the heartbeat. Replicate the
                        // login request here, once per Connect, or a
                        // real FNE connection hangs in WAITING_LOGIN
                        // forever. (The maintenance loop's ping remains
                        // the Core service's job once RUNNING.)
                        byte[] res = new byte[8];
                        FneUtils.StringToBytes(Constants.TAG_REPEATER_LOGIN, res, 0, 4);
                        FneUtils.WriteBytes(fne.PeerId, ref res, 4);
                        fne.SendMasterTraffic(FneBase.CreateOpcode(Constants.NET_FUNC_RPTL), res);
                    }
                }
                catch
                {
                    // The login completes asynchronously via fnecore's
                    // own ACK path; a failed start is surfaced through
                    // PeerDisconnected, never thrown into the caller.
                }
            });
        }

        /// <inheritdoc />
        public void Disconnect()
        {
            background(() =>
            {
                try
                {
                    // Stop blocks on a dead network — that is why the
                    // teardown is backgrounded.
                    if (fne.IsStarted)
                    {
                        fne.Stop();
                    }
                }
                catch
                {
                    // Teardown is best-effort; connection loss is
                    // surfaced through PeerDisconnected.
                }
            });
        }

        /// <inheritdoc />
        public void Ping()
        {
            // WPF parity FnePeer.cs:1534-1535: one-byte ping message.
            // The acknowledged side is delivered through the
            // NetworkFrameHandler PONG detection (validated offset-18
            // direct read, no per-frame header allocation).
            fne.SendMasterTraffic(
                FneBase.CreateOpcode(Constants.NET_FUNC_PING, Constants.NET_SUBFUNC_NOP),
                new byte[1],
                Constants.RtpCallEndSeq);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            background(() =>
            {
                try
                {
                    if (fne.IsStarted)
                    {
                        fne.Stop();
                    }
                }
                catch
                {
                    // Best-effort teardown; never throw from dispose.
                }
            });
        }

        /* ------------------------------------------------------------------
        ** FneSystemBase receive/validation overrides
        ** ---------------------------------------------------------------- */

        /// <inheritdoc />
        protected override bool DMRDataValidate(uint peerId, uint srcId, uint dstId, byte slot, CallType callType, FrameType frameType, DMRDataType dataType, uint streamId, byte[] message)
            => true;

        /// <inheritdoc />
        protected override void DMRDataReceived(object sender, DMRDataReceivedEvent e)
        {
            try
            {
                DmrFrameReceived?.Invoke(e);
            }
            catch
            {
                // Subscriber exceptions are swallowed at the adapter
                // boundary — fnecore's async void listen loop must never
                // see them.
            }
        }

        /// <inheritdoc />
        protected override bool P25DataValidate(uint peerId, uint srcId, uint dstId, CallType callType, P25DUID duid, FrameType frameType, uint streamId, byte[] message)
            => true;

        /// <inheritdoc />
        protected override void P25DataPreprocess(object sender, P25DataReceivedEvent e)
        {
            // No preprocessing needed; raw frames are re-raised as-is.
        }

        /// <inheritdoc />
        protected override void P25DataReceived(object sender, P25DataReceivedEvent e)
        {
            try
            {
                P25FrameReceived?.Invoke(e);
            }
            catch
            {
                // Subscriber exceptions are swallowed at the adapter
                // boundary — fnecore's async void listen loop must never
                // see them.
            }
        }

        /// <inheritdoc />
        protected override bool NXDNDataValidate(uint peerId, uint srcId, uint dstId, CallType callType, NXDNMessageType messageType, FrameType frameType, uint streamId, byte[] message)
            => true;

        /// <inheritdoc />
        protected override void NXDNDataReceived(object sender, NXDNDataReceivedEvent e)
        {
            // NXDN is not routed by this console slice.
        }

        /// <inheritdoc />
        protected override bool PeerIgnored(uint peerId, uint srcId, uint dstId, byte slot, CallType callType, FrameType frameType, DMRDataType dataType, uint streamId)
            => false;

        /// <inheritdoc />
        protected override void PeerConnected(object sender, PeerConnectedEvent e)
        {
            // Fired from fnecore's async void ListenTraffic (MST_ACK case,
            // FnePeer.cs:1213): a throwing subscriber must not crash the
            // process — isolate like every other subscriber invocation.
            try
            {
                peerConnectedHandlers?.Invoke();
            }
            catch
            {
                // Subscriber exceptions are swallowed at the adapter
                // boundary (fnecore's listen loop catches only
                // InvalidOperationException/SocketException).
            }
        }

        /// <inheritdoc />
        protected override void KeyResponse(object sender, KeyResponseEvent e)
        {
            // Key responses are not surfaced by this slice.
        }

        /* ------------------------------------------------------------------
        ** Public send-path re-exposures (WPF FneSystemBase.DMR.cs:75 /
        ** FneSystemBase.P25.cs:220,400 re-expose pattern)
        ** ---------------------------------------------------------------- */

        /// <summary>
        /// Sends one master-traffic packet through the underlying peer
        /// (WPF parity <c>FnePeer.SendMasterTraffic</c>). The
        /// <see cref="Audio.FnecoreVoiceTrafficSender"/> default sink
        /// delivers its assembled packets through this re-exposure.
        /// </summary>
        /// <param name="opcode">The network protocol opcode tuple.</param>
        /// <param name="data">The assembled network frame payload.</param>
        /// <param name="seq">The RTP packet sequence.</param>
        /// <param name="streamId">The transmit stream id.</param>
        public void SendMasterTraffic(Tuple<byte, byte> opcode, byte[] data, ushort seq, uint streamId)
            => fne.SendMasterTraffic(opcode, data, seq, streamId);

        /// <summary>
        /// Creates a 55-byte DMR network frame. WPF parity
        /// FneSystemBase.DMR.cs:75-86.
        /// </summary>
        public void CreateDMRMessage(ref byte[] data, uint srcId, uint dstId, byte slot, FrameType frameType, byte seqNo, byte n)
        {
            RemoteCallData callData = new RemoteCallData
            {
                SrcId = srcId,
                DstId = dstId,
                FrameType = frameType,
                Slot = slot,
            };

            CreateDMRMessage(ref data, callData, seqNo, n);
        }

        /// <summary>
        /// Packs the 225-byte LDU1 into the DFSI records of a 200-byte
        /// P25 network frame (header already written). WPF parity
        /// FneSystemBase.P25.cs:220-263.
        /// </summary>
        /// <param name="netLDU1">The 225-byte decoded LDU1.</param>
        /// <param name="data">The 200-byte network frame.</param>
        /// <param name="srcId">Source radio id (WPF signature parity).</param>
        /// <param name="dstId">Destination talkgroup id (WPF signature parity).</param>
        public void CreateP25LDU1Message(in byte[] netLDU1, ref byte[] data, uint srcId, uint dstId)
        {
            int count = P25_MSG_HDR_SIZE;
            byte[] imbe = new byte[P25ImbeLengthBytes];

            Buffer.BlockCopy(netLDU1, 10, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 24, imbe, P25DFSI.P25_DFSI_LDU1_VOICE1, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE1_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 26, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 46, imbe, P25DFSI.P25_DFSI_LDU1_VOICE2, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE2_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 55, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 60, imbe, P25DFSI.P25_DFSI_LDU1_VOICE3, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE3_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 80, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 77, imbe, P25DFSI.P25_DFSI_LDU1_VOICE4, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE4_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 105, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 94, imbe, P25DFSI.P25_DFSI_LDU1_VOICE5, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE5_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 130, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 111, imbe, P25DFSI.P25_DFSI_LDU1_VOICE6, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE6_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 155, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 128, imbe, P25DFSI.P25_DFSI_LDU1_VOICE7, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE7_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 180, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 145, imbe, P25DFSI.P25_DFSI_LDU1_VOICE8, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE8_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 204, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu1(ref data, 162, imbe, P25DFSI.P25_DFSI_LDU1_VOICE9, srcId, dstId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE9_FRAME_LENGTH_BYTES;

            data[23U] = (byte)count;
        }

        /// <summary>
        /// Packs the 225-byte LDU2 into the DFSI records of a 200-byte
        /// P25 network frame (header already written). WPF parity
        /// FneSystemBase.P25.cs:400-446.
        /// </summary>
        /// <param name="netLDU2">The 225-byte decoded LDU2.</param>
        /// <param name="data">The 200-byte network frame.</param>
        /// <param name="cryptoParams">Encryption parameters, or null for unencrypted.</param>
        public void CreateP25LDU2Message(in byte[] netLDU2, ref byte[] data, CryptoParams? cryptoParams = null)
        {
            cryptoParams ??= new CryptoParams();

            int count = P25_MSG_HDR_SIZE;
            byte[] imbe = new byte[P25ImbeLengthBytes];

            Buffer.BlockCopy(netLDU2, 10, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 24, imbe, P25DFSI.P25_DFSI_LDU2_VOICE10, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE10_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 26, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 46, imbe, P25DFSI.P25_DFSI_LDU2_VOICE11, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE11_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 55, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 60, imbe, P25DFSI.P25_DFSI_LDU2_VOICE12, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE12_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 80, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 77, imbe, P25DFSI.P25_DFSI_LDU2_VOICE13, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE13_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 105, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 94, imbe, P25DFSI.P25_DFSI_LDU2_VOICE14, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE14_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 130, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 111, imbe, P25DFSI.P25_DFSI_LDU2_VOICE15, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE15_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 155, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 128, imbe, P25DFSI.P25_DFSI_LDU2_VOICE16, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE16_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 180, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 145, imbe, P25DFSI.P25_DFSI_LDU2_VOICE17, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE17_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 204, imbe, 0, P25ImbeLengthBytes);
            EncodeLdu2(ref data, 162, imbe, P25DFSI.P25_DFSI_LDU2_VOICE18, cryptoParams);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE18_FRAME_LENGTH_BYTES;

            data[23U] = (byte)count;
        }

        /// <summary>
        /// IMBE codeword length in bytes.
        /// </summary>
        private const int P25ImbeLengthBytes = 11;

        /// <summary>
        /// Encodes one LDU1 DFSI record (WPF parity
        /// FneSystemBase.P25.cs:93-213): the link-control bytes carry
        /// the group LCO, destination talkgroup and source radio id.
        /// </summary>
        private void EncodeLdu1(ref byte[] data, int offset, byte[] imbe, byte frameType, uint srcId, uint dstId)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (imbe is null)
            {
                throw new ArgumentNullException(nameof(imbe));
            }

            uint frameLength = P25DFSI.P25_DFSI_LDU1_VOICE1_FRAME_LENGTH_BYTES;
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU1_VOICE1:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE1_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE2:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE2_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE3:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE3_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE4:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE4_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE5:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE5_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE6:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE6_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE7:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE7_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE8:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE8_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE9:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE9_FRAME_LENGTH_BYTES;
                    break;
                default:
                    return;
            }

            byte[] dfsiFrame = new byte[frameLength];

            dfsiFrame[0U] = frameType;                                                  // Frame Type

            // different frame types mean different things
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU1_VOICE2:
                    {
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 1, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE3:
                    {
                        dfsiFrame[1U] = P25Defines.LC_GROUP;                                // LCO
                        dfsiFrame[2U] = 0;                                                  // MFId
                        dfsiFrame[3U] = 0;                                                  // Service Options
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);        // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE4:
                    {
                        dfsiFrame[1U] = (byte)((dstId >> 16) & 0xFFU);                      // Talkgroup Address
                        dfsiFrame[2U] = (byte)((dstId >> 8) & 0xFFU);
                        dfsiFrame[3U] = (byte)((dstId >> 0) & 0xFFU);
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);        // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE5:
                    {
                        dfsiFrame[1U] = (byte)((srcId >> 16) & 0xFFU);                      // Source Address
                        dfsiFrame[2U] = (byte)((srcId >> 8) & 0xFFU);
                        dfsiFrame[3U] = (byte)((srcId >> 0) & 0xFFU);
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);        // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE6:
                case P25DFSI.P25_DFSI_LDU1_VOICE7:
                case P25DFSI.P25_DFSI_LDU1_VOICE8:
                    {
                        dfsiFrame[1U] = 0;                                              // RS (24,12,13)
                        dfsiFrame[2U] = 0;                                              // RS (24,12,13)
                        dfsiFrame[3U] = 0;                                              // RS (24,12,13)
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE9:
                    {
                        dfsiFrame[1U] = 0;                                              // LSD MSB
                        dfsiFrame[2U] = 0;                                              // LSD LSB
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 4, P25ImbeLengthBytes);   // IMBE
                    }
                    break;

                case P25DFSI.P25_DFSI_LDU1_VOICE1:
                default:
                    {
                        dfsiFrame[6U] = 0;                                              // RSSI
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 10, P25ImbeLengthBytes);  // IMBE
                    }
                    break;
            }

            Buffer.BlockCopy(dfsiFrame, 0, data, offset, (int)frameLength);
        }

        /// <summary>
        /// Encodes one LDU2 DFSI record (WPF parity
        /// FneSystemBase.P25.cs:273-392).
        /// </summary>
        private void EncodeLdu2(ref byte[] data, int offset, byte[] imbe, byte frameType, CryptoParams cryptoParams)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (imbe is null)
            {
                throw new ArgumentNullException(nameof(imbe));
            }

            uint frameLength = P25DFSI.P25_DFSI_LDU2_VOICE10_FRAME_LENGTH_BYTES;
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU2_VOICE10:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE10_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE11:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE11_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE12:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE12_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE13:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE13_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE14:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE14_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE15:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE15_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE16:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE16_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE17:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE17_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE18:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE18_FRAME_LENGTH_BYTES;
                    break;
                default:
                    return;
            }

            byte[] dfsiFrame = new byte[frameLength];

            dfsiFrame[0U] = frameType;                                                  // Frame Type

            // different frame types mean different things
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU2_VOICE11:
                    {
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 1, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE12:
                    {
                        dfsiFrame[1U] = cryptoParams.MI[0];                             // Message Indicator
                        dfsiFrame[2U] = cryptoParams.MI[1];
                        dfsiFrame[3U] = cryptoParams.MI[2];
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE13:
                    {
                        dfsiFrame[1U] = cryptoParams.MI[3];                             // Message Indicator
                        dfsiFrame[2U] = cryptoParams.MI[4];
                        dfsiFrame[3U] = cryptoParams.MI[5];
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE14:
                    {
                        dfsiFrame[1U] = cryptoParams.MI[6];                             // Message Indicator
                        dfsiFrame[2U] = cryptoParams.MI[7];
                        dfsiFrame[3U] = cryptoParams.MI[8];
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE15:
                    {
                        dfsiFrame[1U] = cryptoParams.AlgoId;                            // Algorithm ID
                        FneUtils.WriteBytes(cryptoParams.KeyId, ref dfsiFrame, 2);      // Key ID
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE16:
                    {
                        // first 3 bytes of frame are supposed to be
                        // part of the RS(24, 16, 9) of the VOICE12, 13, 14, 15
                        // control bytes
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE17:
                    {
                        // first 3 bytes of frame are supposed to be
                        // part of the RS(24, 16, 9) of the VOICE12, 13, 14, 15
                        // control bytes
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, P25ImbeLengthBytes);   // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE18:
                    {
                        dfsiFrame[1U] = 0;                                              // LSD MSB
                        dfsiFrame[2U] = 0;                                              // LSD LSB
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 4, P25ImbeLengthBytes);   // IMBE
                    }
                    break;

                case P25DFSI.P25_DFSI_LDU2_VOICE10:
                default:
                    {
                        dfsiFrame[6U] = 0;                                              // RSSI
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 10, P25ImbeLengthBytes);  // IMBE
                    }
                    break;
            }

            Buffer.BlockCopy(dfsiFrame, 0, data, offset, (int)frameLength);
        }
    }
}
