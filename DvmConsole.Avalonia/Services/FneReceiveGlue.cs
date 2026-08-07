// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using DvmConsole.Platform.Audio;
using fnecore;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Routes received FNE frames into the talkgroup audio router:
    /// adapter <see cref="FnecorePeerAdapter.DmrFrameReceived"/> /
    /// <see cref="FnecorePeerAdapter.P25FrameReceived"/> events are
    /// classified with <see cref="FneFrameMapper"/> and voice frames
    /// are forwarded as <see cref="VoiceMode.Dmr"/> /
    /// <see cref="VoiceMode.P25"/> audio units keyed by the WPF-parity
    /// talkgroup key. Terminators are dropped — the router's idle shed
    /// ends the pipeline (zero router changes). Dispose detaches the
    /// routing delegate and clears the call-frame observers, so frames
    /// arriving after the shell closes are silent no-ops.
    /// </summary>
    public sealed class FneReceiveGlue : IDisposable
    {
        private Action<string, ReadOnlyMemory<byte>, VoiceMode>? route;

        /// <summary>
        /// Creates the glue over the given routing delegate.
        /// </summary>
        /// <param name="route">The routing delegate (talkgroup key, audio frame, voice mode).</param>
        public FneReceiveGlue(Action<string, ReadOnlyMemory<byte>, VoiceMode> route)
        {
            this.route = route ?? throw new ArgumentNullException(nameof(route));
        }

        /// <summary>
        /// Additive call-history seam: raised for every CLASSIFIED
        /// receive frame — voice AND terminator — after classification
        /// succeeds and before routing. Control frames (frames
        /// <see cref="FneFrameMapper.TryExtractDmr"/> /
        /// <see cref="FneFrameMapper.TryExtractP25"/> return false for)
        /// stay silent. Routing behavior is untouched: terminators are
        /// still dropped from routing and voice frames are still routed.
        /// </summary>
        public event Action<ReceivedCallMetadata>? CallFrameObserved;

        /// <summary>
        /// Classifies one DMR receive event and routes voice frames to
        /// the router; terminators and other control frames are dropped.
        /// </summary>
        /// <param name="systemName">The FNE system name the frame arrived on.</param>
        /// <param name="e">The raw DMR receive event.</param>
        public void OnDmrFrame(string systemName, DMRDataReceivedEvent e)
        {
            if (!FneFrameMapper.TryExtractDmr(e, out var ambe, out var terminator))
            {
                return;
            }

            CallFrameObserved?.Invoke(new ReceivedCallMetadata(
                systemName,
                e.SrcId,
                e.DstId,
                e.Slot,
                VoiceMode.Dmr,
                e.StreamId,
                FneFrameMapper.BuildDmrTalkgroupKey(systemName, e.DstId, e.Slot),
                terminator));

            var route = this.route;
            if (route is null || terminator || ambe is null)
            {
                return;
            }

            route(FneFrameMapper.BuildDmrTalkgroupKey(systemName, e.DstId, e.Slot), ambe, VoiceMode.Dmr);
        }

        /// <summary>
        /// Classifies one P25 receive event and routes voice LDUs to
        /// the router; TDU/TDULC terminators are dropped.
        /// </summary>
        /// <param name="systemName">The FNE system name the frame arrived on.</param>
        /// <param name="e">The raw P25 receive event.</param>
        public void OnP25Frame(string systemName, P25DataReceivedEvent e)
        {
            if (!FneFrameMapper.TryExtractP25(e, out var ldu, out var terminator))
            {
                return;
            }

            CallFrameObserved?.Invoke(new ReceivedCallMetadata(
                systemName,
                e.SrcId,
                e.DstId,
                0,
                VoiceMode.P25,
                e.StreamId,
                FneFrameMapper.BuildP25TalkgroupKey(systemName, e.DstId),
                terminator));

            var route = this.route;
            if (route is null || terminator || ldu is null)
            {
                return;
            }

            route(FneFrameMapper.BuildP25TalkgroupKey(systemName, e.DstId), ldu, VoiceMode.P25);
        }

        /// <summary>
        /// Detaches the routing delegate and clears the
        /// <see cref="CallFrameObserved"/> subscribers: frames arriving
        /// after disposal are silent no-ops — neither observed nor
        /// routed. Idempotent.
        /// </summary>
        public void Dispose()
        {
            route = null;
            CallFrameObserved = null;
        }
    }
}
