// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the fnecore transport wiring slice:
*
*   DvmConsole.Avalonia.Services.FnecoreTransportFactory
*   DvmConsole.Avalonia.Services.FnecorePeerAdapter
*   DvmConsole.Avalonia.Audio.FnecoreVoiceTrafficSender
*   DvmConsole.Avalonia.Services.FneReceiveGlue
*
* The adapter is the real IFneTransport over fnecore.FnePeer
* (WPF PeerSystem parity). Connect/Disconnect/Dispose are
* BACKGROUNDED through an injectable Action<Action> seam (default
* Task.Run) because FnePeer.Stop() blocks on a dead network — the
* deferred non-blocking decision, resolved as adapter-internal
* backgrounding (contract, service, bridge, and Core tests untouched).
* StartWithoutMaintainence() is used (the Core service owns the
* heartbeat); PONG frames are translated to PingAcknowledged via
* RtpFNEHeader.Decode in the NetworkFrameHandler (no fork change);
* receive events are re-raised as DmrFrameReceived/P25FrameReceived
* for the shell glue.
*
* FnecoreVoiceTrafficSender assembles WPF-exact DMR packets (seqNo 0
* emits VOICE_LC_HEADER then VOICE with EMB/LCSS) and P25 LDU
* messages (isLdu2 derived from seqNo parity — the recorded router
* gap), through an injectable packet sink for headless testing.
*
* FneReceiveGlue subscribes adapter frame events, maps via
* FneFrameMapper, and routes audio frames into the talkgroup router;
* terminators are dropped (the router's 2 s idle shed ends the
* pipeline — zero router changes).
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Audio;
using DvmConsole.Avalonia.Services;
using dvmconsole;
using DvmConsole.Core.Networking;
using DvmConsole.Platform.Audio;
using fnecore;
using fnecore.DMR;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the fnecore transport wiring slice.
    /// </summary>
    public sealed class FnecoreTransportTests
    {
        /* ------------------------------------------------------------------
        ** Test doubles
        ** ---------------------------------------------------------------- */

        private sealed class BackgroundSpy
        {
            public readonly List<Action> Actions = new();

            public void Background(Action action) => Actions.Add(action);

            public void RunAll()
            {
                foreach (var a in Actions.ToList())
                {
                    a();
                }
            }
        }

        private sealed class PacketSink : IPacketSink
        {
            public readonly List<(Tuple<byte, byte> Opcode, byte[] Payload, ushort Seq, uint StreamId)> Sent = new();

            public void Send(Tuple<byte, byte> opcode, byte[] payload, ushort seq, uint streamId)
                => Sent.Add((opcode, payload, seq, streamId));
        }

        private static Codeplug.System MakeSystem(string name = "Test Sys")
            => new Codeplug.System
            {
                Name = name,
                Identity = "Console 1",
                Address = "127.0.0.1",
                Port = 62031,
                PeerId = 1000001,
                Password = "pw",
                Encrypted = false,
            };

        /* ------------------------------------------------------------------
        ** Factory
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Factory_Create_ReturnsTransport_AndRegistersAdapter()
        {
            var factory = new FnecoreTransportFactory();
            var system = MakeSystem();

            var transport = factory.Create(system);

            Assert.NotNull(transport);
            Assert.IsAssignableFrom<IFneTransport>(transport);
            Assert.Same(transport, factory.ResolveAdapter("Test Sys"));
            Assert.Null(factory.ResolveAdapter("Unknown"));
        }

        /* ------------------------------------------------------------------
        ** Adapter lifecycle (backgrounded)
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Adapter_Connect_IsBackgrounded_NeverSynchronous()
        {
            var spy = new BackgroundSpy();
            var adapter = new FnecorePeerAdapter(MakeSystem(), spy.Background);

            adapter.Connect();

            Assert.Single(spy.Actions); // Start deferred, not run inline
        }

        [Fact]
        public void Adapter_BackgroundAction_RunsStartWithoutMaintainence()
        {
            var spy = new BackgroundSpy();
            var adapter = new FnecorePeerAdapter(MakeSystem(), spy.Background);

            adapter.Connect();
            spy.RunAll(); // must not throw

            // Connect does not raise PeerConnected synchronously (login
            // completes asynchronously via fnecore's own ACK path).
            Assert.NotNull(adapter);
        }

        [Fact]
        public void Adapter_DisconnectAndDispose_AreBackgrounded()
        {
            var spy = new BackgroundSpy();
            var adapter = new FnecorePeerAdapter(MakeSystem(), spy.Background);

            adapter.Connect();
            spy.RunAll();
            spy.Actions.Clear();

            adapter.Disconnect();
            adapter.Dispose();

            Assert.Equal(2, spy.Actions.Count); // Stop + Stop (dispose guard)
        }

        [Fact]
        public void Adapter_Dispose_IsIdempotent()
        {
            var spy = new BackgroundSpy();
            var adapter = new FnecorePeerAdapter(MakeSystem(), spy.Background);

            adapter.Connect();
            spy.RunAll();
            spy.Actions.Clear();

            adapter.Dispose();
            adapter.Dispose();

            Assert.Single(spy.Actions); // second Dispose is a no-op
        }

        [Fact]
        public void Adapter_ConnectAfterDispose_DoesNotStartPeer()
        {
            // The Connect action is deferred; if it runs after Dispose's
            // teardown completed, it must NOT start the peer (a leaked
            // running peer holds listen threads + the UDP socket with no
            // heartbeat owner). The disposed guard is the fix.
            var spy = new BackgroundSpy();
            var adapter = new FnecorePeerAdapter(MakeSystem(), spy.Background);

            adapter.Connect();      // action 0: StartWithoutMaintainence
            adapter.Dispose();      // action 1: Stop (guarded)

            // Run the teardown first, THEN the deferred Connect action —
            // the race the guard closes.
            spy.Actions[1]();
            spy.Actions[0]();

            var baseAdapter = (fnecore.FneSystemBase)adapter;
            Assert.False(baseAdapter.IsStarted); // must stay stopped
        }

        [Fact]
        public async Task Factory_ConcurrentCreateAndResolve_NoCorruption()
        {
            // Create runs on the UI thread AND the restart-scheduler pool;
            // ResolveAdapter runs on the audio capture thread during PTT.
            // The registry must tolerate concurrent access.
            var factory = new FnecoreTransportFactory();
            var errors = new List<Exception>();
            var tasks = new List<Task>();

            for (var i = 0; i < 8; i++)
            {
                var name = $"Sys {i}";
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (var j = 0; j < 50; j++)
                        {
                            var t = factory.Create(MakeSystem(name));
                            _ = factory.ResolveAdapter(name);
                            _ = factory.ResolveAdapter("Unknown");
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors)
                        {
                            errors.Add(ex);
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Empty(errors); // no InvalidOperationException / corruption
            Assert.NotNull(factory.ResolveAdapter("Sys 0"));
        }

        [Fact]
        public void Adapter_ThrowingSubscriber_DoesNotEscape()
        {
            // Subscribers are invoked from fnecore's async void listen
            // loops; a throwing subscriber must not crash the process or
            // escape into the caller. The adapter isolates invocation.
            var adapter = new FnecorePeerAdapter(MakeSystem());
            var raised = 0;
            adapter.DmrFrameReceived += _ =>
            {
                raised++;
                throw new InvalidOperationException("subscriber failure");
            };

            // Directly invoke the protected receive override as fnecore's
            // listen loop would (reflection: the adapter is sealed and the
            // override is protected).
            var e = new DMRDataReceivedEvent(
                1, 1001, 31001, 0, CallType.GROUP, FrameType.VOICE,
                DMRDataType.VOICE_LC_HEADER, 0, 0, 1, new byte[55]);
            var method = typeof(fnecore.FneSystemBase).GetMethod(
                "DMRDataReceived",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            var ex = Record.Exception(() =>
                method!.Invoke(adapter, new object[] { adapter, e }));

            Assert.Null(ex); // subscriber exception swallowed by the adapter
            Assert.Equal(1, raised);
        }

        [Fact]
        public void Adapter_ThrowingPeerConnectedSubscriber_DoesNotEscape()
        {
            // PeerConnected fires from fnecore's async void ListenTraffic
            // (MST_ACK case) — a throwing subscriber must not crash the
            // process. The explicit IFneTransport event must isolate.
            var adapter = new FnecorePeerAdapter(MakeSystem());
            var raised = 0;
            ((IFneTransport)adapter).PeerConnected += () =>
            {
                raised++;
                throw new InvalidOperationException("subscriber failure");
            };

            var method = typeof(fnecore.FneSystemBase).GetMethod(
                "PeerConnected",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            var ex = Record.Exception(() =>
                method!.Invoke(adapter, new object[] { adapter, new PeerConnectedEvent(1, new PeerInformation()) }));

            Assert.Null(ex); // subscriber exception swallowed by the adapter
            Assert.Equal(1, raised);
        }

        [Fact]
        public void Adapter_ImplementsIfneTransport_Surface()
        {
            var type = typeof(FnecorePeerAdapter);
            Assert.True(typeof(IFneTransport).IsAssignableFrom(type));
            Assert.True(typeof(fnecore.FneSystemBase).IsAssignableFrom(type));
            Assert.NotNull(type.GetEvent("DmrFrameReceived"));
            Assert.NotNull(type.GetEvent("P25FrameReceived"));
        }

        /* ------------------------------------------------------------------
        ** Voice traffic sender
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Sender_DmrSeq0_EmitsHeaderThenVoice()
        {
            var sink = new PacketSink();
            // A real adapter is needed for the WPF-exact packet assembly;
            // its ctor performs no I/O (FnePeer builds sockets lazily on
            // Start), and the injected sink replaces the actual send.
            var adapter = new FnecorePeerAdapter(MakeSystem());
            var sender = new FnecoreVoiceTrafficSender(
                _ => adapter, sink);

            var target = new TransmitTarget("Test Sys", "31001", 1, VoiceMode.Dmr, 1001);
            sender.SendDmrVoice(target, new byte[27], 1, 0);

            Assert.Equal(2, sink.Sent.Count);
            Assert.NotNull(sink.Sent[0].Payload); // VOICE_LC_HEADER packet
            Assert.NotNull(sink.Sent[1].Payload); // VOICE packet
            Assert.Equal(0, sink.Sent[0].Seq);
            Assert.Equal(0, sink.Sent[1].Seq);
        }

        [Fact]
        public void Sender_P25_IsLdu2FromSeqNoParity()
        {
            var sink = new PacketSink();
            var adapter = new FnecorePeerAdapter(MakeSystem());
            var sender = new FnecoreVoiceTrafficSender(
                _ => adapter, sink);

            var target = new TransmitTarget("Test Sys", "31002", 1, VoiceMode.P25, 1001);
            sender.SendP25Ldu(target, false, new byte[225], 1, 0);
            sender.SendP25Ldu(target, false, new byte[225], 1, 1);

            Assert.Equal(2, sink.Sent.Count);
            // The router always passes isLdu2:false; the sender derives the
            // real alternation from seqNo parity (WPF parity).
            Assert.All(sink.Sent, s => Assert.NotNull(s.Payload));
        }

        [Fact]
        public void Sender_UnknownSystem_NoOp()
        {
            var sink = new PacketSink();
            var sender = new FnecoreVoiceTrafficSender(
                _ => null, sink);

            sender.SendDmrVoice(new TransmitTarget("Missing", "31001", 1, VoiceMode.Dmr, 1001), new byte[27], 1, 0);
            sender.SendP25Ldu(new TransmitTarget("Missing", "31002", 1, VoiceMode.P25, 1001), false, new byte[225], 1, 0);

            Assert.Empty(sink.Sent); // no adapter resolved: silent no-op
        }

        /* ------------------------------------------------------------------
        ** Receive glue
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Glue_DmrFrame_RoutesVoiceToRouter()
        {
            var routed = new List<(string Key, byte[] Frame, VoiceMode Mode)>();
            var glue = new FneReceiveGlue(
                (key, frame, mode) => routed.Add((key, frame.ToArray(), mode)));

            var message = new byte[55];
            message[13] = 0x50;
            message[19] = 0x0F;
            for (var i = 20; i < 33; i++)
            {
                message[i] = (byte)i;
            }

            var e = new DMRDataReceivedEvent(
                1, 1001, 31001, 0, CallType.GROUP, FrameType.VOICE, DMRDataType.VOICE_LC_HEADER,
                0, 0, 1, message);
            glue.OnDmrFrame("Test Sys", e);

            var routedFrame = Assert.Single(routed);
            Assert.Equal("test sys|31001|slot:0", routedFrame.Key);
            Assert.Equal(VoiceMode.Dmr, routedFrame.Mode);
            Assert.Equal(27, routedFrame.Frame.Length);
        }

        [Fact]
        public void Glue_DmrTerminator_Dropped()
        {
            var routed = new List<(string Key, byte[] Frame, VoiceMode Mode)>();
            var glue = new FneReceiveGlue(
                (key, frame, mode) => routed.Add((key, frame.ToArray(), mode)));

            var e = new DMRDataReceivedEvent(
                1, 1001, 31001, 0, CallType.GROUP, FrameType.DATA_SYNC,
                DMRDataType.TERMINATOR_WITH_LC, 0, 0, 1, new byte[55]);
            glue.OnDmrFrame("Test Sys", e);

            Assert.Empty(routed); // terminator dropped, idle shed ends the pipeline
        }

        [Fact]
        public void Glue_P25Frame_RoutesVoiceToRouter()
        {
            var routed = new List<(string Key, byte[] Frame, VoiceMode Mode)>();
            var glue = new FneReceiveGlue(
                (key, frame, mode) => routed.Add((key, frame.ToArray(), mode)));

            var message = new byte[200];
            message[22] = 0x00;
            message[23] = 200;
            byte[] sig = { 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A };
            int[] recordOffsets = { 0, 22, 36, 53, 70, 87, 104, 121, 138 };
            for (var r = 0; r < 9; r++)
            {
                message[24 + recordOffsets[r]] = sig[r];
            }

            var e = new P25DataReceivedEvent(
                1, 1001, 31001, CallType.GROUP, P25DUID.LDU1, FrameType.VOICE, 0, 1, message);
            glue.OnP25Frame("Test Sys", e);

            var routedFrame = Assert.Single(routed);
            Assert.Equal("test sys|31001", routedFrame.Key);
            Assert.Equal(VoiceMode.P25, routedFrame.Mode);
            Assert.Equal(225, routedFrame.Frame.Length);
        }

        /* ------------------------------------------------------------------
        ** Surface shapes
        ** ---------------------------------------------------------------- */

        [Fact]
        public void FneReceiveGlue_IsPublicSealed_WithFrameEntryPoints()
        {
            var type = typeof(FneReceiveGlue);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(Action<string, ReadOnlyMemory<byte>, VoiceMode>) }));
            Assert.NotNull(type.GetMethod("OnDmrFrame"));
            Assert.NotNull(type.GetMethod("OnP25Frame"));
        }

        [Fact]
        public void FnecoreVoiceTrafficSender_IsPublicSealed()
        {
            var type = typeof(FnecoreVoiceTrafficSender);
            Assert.True(type.IsSealed);
            Assert.True(typeof(IVoiceTrafficSender).IsAssignableFrom(type));
        }
    }
}
