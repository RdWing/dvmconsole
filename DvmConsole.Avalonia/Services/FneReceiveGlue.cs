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
    /// routing delegate so frames arriving after the shell closes are
    /// no-ops.
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
        /// Classifies one DMR receive event and routes voice frames to
        /// the router; terminators and other control frames are dropped.
        /// </summary>
        /// <param name="systemName">The FNE system name the frame arrived on.</param>
        /// <param name="e">The raw DMR receive event.</param>
        public void OnDmrFrame(string systemName, DMRDataReceivedEvent e)
        {
            var route = this.route;
            if (route is null
                || !FneFrameMapper.TryExtractDmr(e, out var ambe, out var terminator)
                || terminator
                || ambe is null)
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
            var route = this.route;
            if (route is null
                || !FneFrameMapper.TryExtractP25(e, out var ldu, out var terminator)
                || terminator
                || ldu is null)
            {
                return;
            }

            route(FneFrameMapper.BuildP25TalkgroupKey(systemName, e.DstId), ldu, VoiceMode.P25);
        }

        /// <summary>
        /// Detaches the routing delegate: frames routed after disposal
        /// are silent no-ops. Idempotent.
        /// </summary>
        public void Dispose() => route = null;
    }
}
