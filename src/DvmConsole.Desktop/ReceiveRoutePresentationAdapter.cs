// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;
using System.Runtime.CompilerServices;
using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

// Maps shared route decisions to the existing desktop facade. Operational route
// membership, termination, tombstones and target selection live in Application.
internal sealed class ReceiveRoutePresentationAdapter
{
    private readonly ReceiveRouteCoordinator routes;
    private readonly ConditionalWeakTable<IReadOnlyList<ChannelViewModel>, StateList> stateLists = new();
    private TrackingAdapter? trackingAdapter;
    private ActivityAdapter? audioAdapter;
    private ActivityAdapter? patchAdapter;
    private readonly Dictionary<ChannelId, ChannelViewModel> views;
    private readonly Dictionary<ChannelId, ChannelViewModel[]> singletons;

    public ReceiveRoutePresentationAdapter(
        IReadOnlyDictionary<(FneTrafficProtocol Protocol, uint DestinationId), ChannelViewModel[]> legacyRoutes,
        ConsoleReceiveRouteState? sharedState = null)
    {
        views = legacyRoutes.Values.SelectMany(channels => channels).Distinct().ToDictionary(channel => channel.Id);
        singletons = views.ToDictionary(pair => pair.Key, pair => new[] { pair.Value });
        routes = new ReceiveRouteCoordinator(legacyRoutes.ToDictionary(
            pair => (FneReceiveWorkQueueAdapter.ToRadioProtocol(pair.Key.Protocol), pair.Key.DestinationId),
            pair => pair.Value.Select(channel => channel.SessionState).ToArray()), sharedState);
    }

    private ChannelViewModel View(ConsoleChannelState state) => views[state.Id];
    private StateList States(IReadOnlyList<ChannelViewModel> channels)
        => stateLists.GetValue(channels, static source => new StateList(source));

    private Func<ConsoleChannelState, uint, bool> Tracking(Func<ChannelViewModel, uint, bool> callback)
    {
        var current = Volatile.Read(ref trackingAdapter);
        if (current is not null && current.Source.Equals(callback)) return current.Target;
        var next = new TrackingAdapter(callback, (state, stream) => callback(View(state), stream));
        Volatile.Write(ref trackingAdapter, next);
        return next.Target;
    }

    private Func<ConsoleChannelState, bool> Activity(Func<ChannelViewModel, bool> callback, ref ActivityAdapter? cache)
    {
        var current = Volatile.Read(ref cache);
        if (current is not null && current.Source.Equals(callback)) return current.Target;
        var next = new ActivityAdapter(callback, state => callback(View(state)));
        Volatile.Write(ref cache, next);
        return next.Target;
    }

    private sealed record TrackingAdapter(Func<ChannelViewModel, uint, bool> Source, Func<ConsoleChannelState, uint, bool> Target);
    private sealed record ActivityAdapter(Func<ChannelViewModel, bool> Source, Func<ConsoleChannelState, bool> Target);
    // Retain the source collection rather than copying membership per packet.
    private sealed class StateList(IReadOnlyList<ChannelViewModel> source) : IReadOnlyList<ConsoleChannelState>
    {
        public int Count => source.Count;
        public ConsoleChannelState this[int index] => source[index].SessionState;
        public IEnumerator<ConsoleChannelState> GetEnumerator()
        {
            for (int index = 0; index < source.Count; index++) yield return source[index].SessionState;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private ChannelViewModel[] Views(ConsoleChannelState[] states)
        => states.Length switch
        {
            0 => [],
            1 => singletons[states[0].Id],
            _ => states.Select(View).ToArray()
        };
    private ReceiveDispatchTargets Targets(ReceiveStateTargets states)
        => states.Count switch
        {
            0 => ReceiveDispatchTargets.Empty,
            1 => ReceiveDispatchTargets.One(View(states[0])),
            _ => ReceiveDispatchTargets.FromArray(states.Select(View).ToArray())
        };

    public ReceiveIngressRoutingDecision ObserveIngress(FneTrafficFrame traffic,
        Func<ChannelViewModel, uint, bool> tracking, DateTimeOffset? observedAt = null)
        => routes.ObserveIngress(traffic, Tracking(tracking), observedAt);

    public ChannelViewModel[] ResolveTargets(IReadOnlyList<ChannelViewModel> decodeChannels,
        FneTrafficFrame traffic, ReceiveIngressRoutingDecision decision, Func<ChannelViewModel, uint, bool> tracking)
        => Views(routes.ResolveTargets(States(decodeChannels), traffic, decision,
            Tracking(tracking)));

    public ReceiveDispatchTargets ResolveDispatchTargets(IReadOnlyList<ChannelViewModel> decodeChannels,
        bool includeRecordingChannels, FneTrafficFrame traffic, ReceiveIngressRoutingDecision decision,
        Func<ChannelViewModel, uint, bool> tracking)
        => Targets(routes.ResolveDispatchTargets(States(decodeChannels), includeRecordingChannels, traffic, decision, Tracking(tracking)));

    public ReceiveDispatchTargets ResolveDispatchTargetsById(IReadOnlyList<ChannelId> decodeChannels,
        bool includeRecordingChannels, FneTrafficFrame traffic, ReceiveIngressRoutingDecision decision,
        Func<ChannelViewModel, uint, bool> tracking)
        => Targets(routes.ResolveDispatchTargetsById(decodeChannels, includeRecordingChannels, traffic, decision,
            Tracking(tracking)));

    public ChannelViewModel[] ResolvePresentationCandidates(IReadOnlyList<ChannelViewModel> systemChannels,
        FneTrafficFrame traffic, ReceiveIngressRoutingDecision decision, Func<ChannelViewModel, bool> audio,
        Func<ChannelViewModel, bool> patch, Func<ChannelViewModel, uint, bool> tracking)
        => Views(routes.ResolvePresentationCandidates(States(systemChannels), traffic, decision,
            Activity(audio, ref audioAdapter), Activity(patch, ref patchAdapter), Tracking(tracking)));

    public IReadOnlyList<ReceiveRouteProjectionDecision> Advance(DateTimeOffset now) => routes.Advance(now);
    public bool IsActive(ChannelRouteKey key, uint stream) => routes.IsActive(key, stream);
    public ChannelViewModel? ResolveProjectionTarget(ChannelRouteKey key, uint stream,
        Func<ChannelViewModel, bool> audio, Func<ChannelViewModel, bool> patch)
    {
        var state = routes.ResolveProjectionTarget(key, stream, Activity(audio, ref audioAdapter), Activity(patch, ref patchAdapter));
        return state is null ? null : View(state);
    }
    internal ReceiveRouteProjectionDecision ObserveCompatibility(ChannelViewModel channel, FneTrafficFrame traffic, DateTimeOffset now)
        => routes.ObserveCompatibility(channel.SessionState, traffic, now);
    internal ReceiveRouteProjectionDecision AdvanceCompatibility(ChannelViewModel channel, DateTimeOffset now)
        => routes.AdvanceCompatibility(channel.SessionState, now);
}
