// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using dvmconsole;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Composes the Core patch lifecycle with the Avalonia receive/audio
    /// seams. Core PatchManager is deliberately not thread-safe, so every
    /// lifecycle call and every outbound sender callback is serialized here.
    /// The coordinator owns only composition-local state; Core, Platform,
    /// fnecore, the receive glue, and the audio router remain unchanged.
    /// </summary>
    public sealed class PatchForwardingCoordinator : IDecodedPcmObserver, IDisposable
    {
        private const int DmrCodewordsPerFrame = 3;
        private const int P25CodewordsPerLdu = 9;
        private const int DmrCodewordBytes = 9;
        private const int P25CodewordBytes = 11;
        private const int DmrFrameBytes = DmrCodewordsPerFrame * DmrCodewordBytes;
        private const int P25LduBytes = 225;
        private static readonly int[] P25CodewordOffsets = { 10, 26, 55, 80, 105, 130, 155, 180, 204 };

        private readonly Codeplug codeplug;
        private readonly IVoiceFrameEncoder encoder;
        private readonly IVoiceTrafficSender sender;
        private readonly string membershipContextKey;
        private readonly IDecodedPcmObserver? downstreamObserver;
        private readonly Func<string, bool> isSystemConnected;
        private readonly Func<DateTime> utcNow;
        private readonly object syncRoot = new object();
        private readonly Dictionary<ReceiveIdentity, ReceivedCallMetadata> receiveSessions = new();
        private readonly Dictionary<ForwardIdentity, ForwardState> forwards =
            new ForwardIdentityDictionary();
        private readonly PatchManager patchManager;
        private uint nextStreamId = 1;
        private bool disposed;

        private readonly record struct ReceiveIdentity(string Key, VoiceMode Mode);
        private readonly record struct ForwardIdentity(string SystemName, string Tgid);

        private sealed class ForwardIdentityDictionary : Dictionary<ForwardIdentity, ForwardState>
        {
            public ForwardIdentityDictionary()
                : base(ForwardIdentityComparer.Instance)
            {
            }
        }

        private sealed class ForwardIdentityComparer : IEqualityComparer<ForwardIdentity>
        {
            public static readonly ForwardIdentityComparer Instance = new ForwardIdentityComparer();

            public bool Equals(ForwardIdentity x, ForwardIdentity y)
                => string.Equals(x.SystemName, y.SystemName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Tgid, y.Tgid, StringComparison.Ordinal);

            public int GetHashCode(ForwardIdentity value)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(value.SystemName ?? string.Empty),
                    StringComparer.Ordinal.GetHashCode(value.Tgid ?? string.Empty));
        }

        private sealed class ForwardState
        {
            public ForwardState(TransmitTarget target, uint streamId)
            {
                Target = target;
                StreamId = streamId;
            }

            public TransmitTarget Target { get; set; }
            public uint StreamId { get; }
            public int NextSeqNo { get; set; }
            public bool NextP25Ldu2 { get; set; }
            public List<byte[]> PendingCodewords { get; } = new List<byte[]>();
        }

        /// <summary>
        /// Creates the receive-forwarding coordinator.
        /// </summary>
        /// <param name="codeplug">The loaded codeplug.</param>
        /// <param name="encoder">The per-codeword voice encoder.</param>
        /// <param name="sender">The encoded-unit traffic sender.</param>
        /// <param name="membershipContextKey">The absolute settings context key.</param>
        /// <param name="downstreamObserver">Optional observer, normally TAR, that still receives all decoded PCM.</param>
        /// <param name="isSystemConnected">Optional target-availability predicate; null means available.</param>
        /// <param name="utcNow">Optional clock seam.</param>
        public PatchForwardingCoordinator(
            Codeplug codeplug,
            IVoiceFrameEncoder encoder,
            IVoiceTrafficSender sender,
            string membershipContextKey,
            IDecodedPcmObserver? downstreamObserver = null,
            Func<string, bool>? isSystemConnected = null,
            Func<DateTime>? utcNow = null)
        {
            this.codeplug = codeplug ?? throw new ArgumentNullException(nameof(codeplug));
            this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
            this.membershipContextKey = membershipContextKey ?? throw new ArgumentNullException(nameof(membershipContextKey));
            this.downstreamObserver = downstreamObserver;
            this.isSystemConnected = isSystemConnected ?? (_ => true);
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            patchManager = new PatchManager(
                BeginForward,
                EndForward,
                SendForwardAudio,
                GetFallbackSourceId,
                this.utcNow);
        }

        /// <summary>
        /// Applies the saved group section to the runtime patch engine. Only
        /// enabled patch groups are included; multi-select groups never enter
        /// PatchManager. A replacement is transactional in Core and ends any
        /// forwards removed by the new configuration.
        /// </summary>
        public void ApplySavedMemberships(UserSettingsGroupSection section)
        {
            if (section is null)
                throw new ArgumentNullException(nameof(section));

            lock (syncRoot)
            {
                if (disposed)
                    return;

                patchManager.SetSourceIdPassthrough(codeplug.PatchSourceIdPassthrough);
                patchManager.ApplyMemberships(
                    BuildMemberships(section, out var oneWayModes),
                    oneWayModes);
            }
        }

        /// <summary>
        /// Returns whether the Core patch engine currently owns an outbound
        /// forward for the supplied member identity. Patch PTT uses this
        /// query to avoid sending microphone traffic concurrently to a
        /// receive-forward leg for the same target.
        /// </summary>
        public bool IsForwardTargetActive(string? systemName, string? tgid)
        {
            lock (syncRoot)
            {
                return !disposed
                    && patchManager.IsForwardTargetActive(
                        systemName ?? string.Empty,
                        tgid ?? string.Empty);
            }
        }

        /// <summary>
        /// Feeds classified receive metadata into the Core call lifecycle.
        /// Terminators and idle ends are both idempotent at the coordinator
        /// boundary.
        /// </summary>
        public void HandleReceiveFrame(ReceivedCallMetadata metadata)
        {
            if (metadata is null)
                return;

            lock (syncRoot)
            {
                if (disposed)
                    return;

                var identity = new ReceiveIdentity(metadata.Key ?? string.Empty, metadata.Mode);
                if (metadata.IsTerminator)
                {
                    if (receiveSessions.Remove(identity, out var active))
                        patchManager.HandleCallEnd(ToSystem(active), ToTgid(active), active.StreamId);
                    return;
                }

                if (receiveSessions.TryGetValue(identity, out var previous))
                {
                    if (previous.StreamId == metadata.StreamId)
                    {
                        receiveSessions[identity] = metadata;
                        return;
                    }

                    patchManager.HandleCallEnd(ToSystem(previous), ToTgid(previous), previous.StreamId);
                }

                receiveSessions[identity] = metadata;
                patchManager.HandleCallStart(
                    metadata.SystemName,
                    ToTgid(metadata),
                    metadata.StreamId,
                    metadata.SrcId);
            }
        }

        /// <inheritdoc />
        public void ObserveDecodedPcm(string key, VoiceMode mode, ReadOnlyMemory<byte> pcm)
        {
            try
            {
                downstreamObserver?.ObserveDecodedPcm(key, mode, pcm);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Patch downstream PCM observer failed: {exception.Message}");
            }

            if (pcm.Length != AudioPcm.FrameBytes)
                return;

            lock (syncRoot)
            {
                if (disposed
                    || !receiveSessions.TryGetValue(new ReceiveIdentity(key ?? string.Empty, mode), out var metadata))
                {
                    return;
                }

                patchManager.HandleAudio(
                    ToSystem(metadata),
                    ToTgid(metadata),
                    metadata.StreamId,
                    metadata.SrcId,
                    pcm.ToArray());
            }
        }

        /// <summary>
        /// Ends the active receive stream represented by an idle router
        /// release. Repeated calls and unknown keys are no-ops.
        /// </summary>
        public void HandleStreamEnded(string key, VoiceMode mode)
        {
            lock (syncRoot)
            {
                if (disposed
                    || !receiveSessions.Remove(new ReceiveIdentity(key ?? string.Empty, mode), out var metadata))
                {
                    return;
                }

                patchManager.HandleCallEnd(ToSystem(metadata), ToTgid(metadata), metadata.StreamId);
            }
        }

        /// <summary>
        /// Closes every active forward by applying an empty membership set.
        /// The operation is idempotent and does not require a playback device.
        /// </summary>
        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;

                patchManager.ApplyMemberships(null!);
                receiveSessions.Clear();
                forwards.Clear();
                disposed = true;
            }
        }

        private uint BeginForward(string systemName, string tgid, uint sourceId)
        {
            if (disposed)
                return 0;

            var target = ResolveTarget(systemName, tgid, sourceId);
            if (target is null || !IsAvailable(target.Value.SystemName))
                return 0;

            var identity = new ForwardIdentity(target.Value.SystemName, target.Value.TalkgroupId);
            if (forwards.ContainsKey(identity))
                return 0;

            var streamId = AllocateStreamId();
            var state = new ForwardState(target.Value, streamId);
            forwards.Add(identity, state);

            if (state.Target.Mode == VoiceMode.P25)
            {
                try
                {
                    sender.SendP25Tdu(state.Target, state.StreamId, grantDemand: true);
                }
                catch (Exception exception)
                {
                    forwards.Remove(identity);
                    System.Diagnostics.Debug.WriteLine(
                        $"Patch P25 start failed: {exception.Message}");
                    return 0;
                }
            }

            return streamId;
        }

        private void EndForward(string systemName, string tgid, uint streamId, uint sourceId)
        {
            var identity = new ForwardIdentity(systemName ?? string.Empty, tgid ?? string.Empty);
            if (!forwards.Remove(identity, out var state) || state.StreamId != streamId)
                return;

            var target = sourceId == 0 || sourceId == state.Target.SourceId
                ? state.Target
                : state.Target with { SourceId = sourceId };
            try
            {
                if (target.Mode == VoiceMode.Dmr)
                    sender.SendDmrTerminator(target, state.StreamId, state.NextSeqNo);
                else
                    sender.SendP25Tdu(target, state.StreamId, grantDemand: false);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Patch forward end failed: {exception.Message}");
            }
        }

        private void SendForwardAudio(string systemName, string tgid, byte[] pcm, uint sourceId)
        {
            if (pcm is null || pcm.Length != AudioPcm.FrameBytes)
                return;

            var identity = new ForwardIdentity(systemName ?? string.Empty, tgid ?? string.Empty);
            if (!forwards.TryGetValue(identity, out var state))
                return;

            if (sourceId != 0 && sourceId != state.Target.SourceId)
                state.Target = state.Target with { SourceId = sourceId };

            var samples = VoiceFrameSplitter.BytesToSamples(pcm);
            if (samples.Length == 0)
                return;

            byte[] codeword;
            try
            {
                if (!encoder.TryEncode(state.Target.Mode, samples, out codeword)
                    || codeword is null
                    || codeword.Length != (state.Target.Mode == VoiceMode.Dmr
                        ? DmrCodewordBytes
                        : P25CodewordBytes))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Patch voice encode failed: {exception.Message}");
                return;
            }

            state.PendingCodewords.Add(codeword);
            var required = state.Target.Mode == VoiceMode.Dmr
                ? DmrCodewordsPerFrame
                : P25CodewordsPerLdu;
            while (state.PendingCodewords.Count >= required)
            {
                var unit = state.Target.Mode == VoiceMode.Dmr
                    ? AssembleDmr(state.PendingCodewords)
                    : AssembleP25(state.PendingCodewords);
                state.PendingCodewords.RemoveRange(0, required);

                try
                {
                    if (state.Target.Mode == VoiceMode.Dmr)
                    {
                        sender.SendDmrVoice(state.Target, unit, state.StreamId, state.NextSeqNo++);
                    }
                    else
                    {
                        sender.SendP25Ldu(
                            state.Target,
                            state.NextP25Ldu2,
                            unit,
                            state.StreamId,
                            state.NextSeqNo++);
                        state.NextP25Ldu2 = !state.NextP25Ldu2;
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Patch voice send failed: {exception.Message}");
                }
            }
        }

        private uint GetFallbackSourceId(string systemName, string _)
        {
            var system = FindSystem(systemName);
            return system is not null && uint.TryParse(system.Rid, out var sourceId)
                ? sourceId
                : 0;
        }

        private TransmitTarget? ResolveTarget(string systemName, string tgid, uint sourceId)
        {
            if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(tgid))
                return null;

            var system = FindSystem(systemName);
            if (system is null || !uint.TryParse(system.Rid, out var fallbackSourceId))
                return null;

            var channel = (codeplug.Zones ?? new List<Codeplug.Zone>())
                .Where(zone => zone?.Channels is not null)
                .SelectMany(zone => zone!.Channels!)
                .FirstOrDefault(candidate => candidate is not null
                    && string.Equals(candidate.System, system.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Tgid, tgid, StringComparison.Ordinal));
            if (channel is null || channel.RxOnly)
                return null;

            var mode = channel.GetChannelMode();
            if (mode == Codeplug.ChannelMode.NXDN
                || !uint.TryParse(channel.Tgid, out _))
            {
                return null;
            }

            return new TransmitTarget(
                system.Name,
                channel.Tgid,
                (byte)channel.Slot,
                mode == Codeplug.ChannelMode.P25 ? VoiceMode.P25 : VoiceMode.Dmr,
                sourceId == 0 ? fallbackSourceId : sourceId);
        }

        private Codeplug.System? FindSystem(string systemName)
            => (codeplug.Systems ?? new List<Codeplug.System>())
                .FirstOrDefault(system => system is not null
                    && string.Equals(system.Name, systemName, StringComparison.OrdinalIgnoreCase));

        private bool IsAvailable(string systemName)
        {
            try
            {
                return isSystemConnected(systemName);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Patch target availability check failed: {exception.Message}");
                return false;
            }
        }

        private uint AllocateStreamId()
        {
            var streamId = nextStreamId++;
            if (streamId == 0)
                streamId = nextStreamId++;
            return streamId;
        }

        private Dictionary<string, List<PatchTalkgroupMember>> BuildMemberships(
            UserSettingsGroupSection section,
            out Dictionary<string, bool> oneWayModes)
        {
            var result = new Dictionary<string, List<PatchTalkgroupMember>>(StringComparer.OrdinalIgnoreCase);
            oneWayModes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var savedMemberships = FindContext(section.PatchGroupMemberships);
            var savedModes = FindContext(section.PatchGroupModes);
            var savedEnabled = FindContext(section.PatchGroupEnabledStates);
            var patchGroupNames = new HashSet<string>(
                (codeplug.Groups ?? new List<Codeplug.Group>())
                    .Where(group => group is not null && group.IsPatchGroup())
                    .Select(group => group.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var pair in savedMemberships)
            {
                if (!patchGroupNames.Contains(pair.Key)
                    || pair.Value is null
                    || !savedEnabled.TryGetValue(pair.Key, out var enabled)
                    || !enabled)
                {
                    continue;
                }

                var groupName = patchGroupNames.First(name =>
                    string.Equals(name, pair.Key, StringComparison.OrdinalIgnoreCase));
                result[groupName] = pair.Value;
                if (savedModes.TryGetValue(pair.Key, out var oneWay))
                    oneWayModes[groupName] = oneWay;
            }

            return result;
        }

        private Dictionary<string, T> FindContext<T>(Dictionary<string, Dictionary<string, T>>? values)
        {
            if (values is null)
                return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            if (values.TryGetValue(membershipContextKey, out var exact) && exact is not null)
                return new Dictionary<string, T>(exact, StringComparer.OrdinalIgnoreCase);

            var match = values.FirstOrDefault(pair =>
                string.Equals(pair.Key, membershipContextKey, StringComparison.OrdinalIgnoreCase));
            return match.Value is null
                ? new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, T>(match.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static string ToSystem(ReceivedCallMetadata metadata)
            => metadata.SystemName ?? string.Empty;

        private string ToTgid(ReceivedCallMetadata metadata)
        {
            var matchingChannel = (codeplug.Zones ?? new List<Codeplug.Zone>())
                .Where(zone => zone?.Channels is not null)
                .SelectMany(zone => zone!.Channels!)
                .FirstOrDefault(channel => channel is not null
                    && string.Equals(channel.System, metadata.SystemName, StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(channel.Tgid, out var channelTgid)
                    && channelTgid == metadata.DstId
                    && (metadata.Mode == VoiceMode.P25
                        || channel.Slot == metadata.Slot + 1));

            return matchingChannel?.Tgid
                ?? metadata.DstId.ToString(CultureInfo.InvariantCulture);
        }

        private static byte[] AssembleDmr(IReadOnlyList<byte[]> codewords)
        {
            var frame = new byte[DmrFrameBytes];
            for (var i = 0; i < DmrCodewordsPerFrame; i++)
                Buffer.BlockCopy(codewords[i], 0, frame, i * DmrCodewordBytes, DmrCodewordBytes);
            return frame;
        }

        private static byte[] AssembleP25(IReadOnlyList<byte[]> codewords)
        {
            var ldu = new byte[P25LduBytes];
            for (var i = 0; i < P25CodewordsPerLdu; i++)
                Buffer.BlockCopy(codewords[i], 0, ldu, P25CodewordOffsets[i], P25CodewordBytes);
            return ldu;
        }
    }
}
