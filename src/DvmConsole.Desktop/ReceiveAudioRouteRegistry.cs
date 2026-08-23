using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DvmConsole.Audio;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal sealed record ReceiveAudioRoute(
    string DeviceId,
    IAudioBackend Backend,
    AudioMixer Mixer);

internal sealed class ReceiveAudioRouteRegistry
{
    private readonly object sessionRouteSync = new();
    private readonly ConcurrentDictionary<ChannelViewModel, string> sessionRoutes = [];
    private readonly Dictionary<string, ChannelViewModel[]> sessionRouteSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ChannelViewModel, bool> sessionFollowsSystemDefault = [];
    private readonly ConcurrentDictionary<string, ReceiveAudioRoute> routes =
        new(StringComparer.OrdinalIgnoreCase);

    public ReceiveAudioRoute[] RouteSnapshot => routes.Values.ToArray();

    public bool TryGetRoute(
        string deviceId,
        [NotNullWhen(true)] out ReceiveAudioRoute? route)
        => routes.TryGetValue(deviceId, out route);

    public bool TryGetRoute(
        ChannelViewModel channel,
        [NotNullWhen(true)] out ReceiveAudioRoute? route)
    {
        route = null;
        return sessionRoutes.TryGetValue(channel, out string? routeId) &&
            routes.TryGetValue(routeId, out route);
    }

    public bool TryAddRoute(ReceiveAudioRoute route)
        => routes.TryAdd(route.DeviceId, route);

    public bool TryRemoveRoute(
        string deviceId,
        [NotNullWhen(true)] out ReceiveAudioRoute? route)
        => routes.TryRemove(deviceId, out route);

    public bool TryAddSessionRoute(ChannelViewModel channel, string deviceId)
    {
        lock (sessionRouteSync)
        {
            if (!sessionRoutes.TryAdd(channel, deviceId))
                return false;

            sessionRouteSnapshots.TryGetValue(deviceId, out ChannelViewModel[]? current);
            var updated = new ChannelViewModel[(current?.Length ?? 0) + 1];
            current?.CopyTo(updated, 0);
            updated[^1] = channel;
            sessionRouteSnapshots[deviceId] = updated;
            return true;
        }
    }

    public bool TryRemoveSessionRoute(ChannelViewModel channel, out string? deviceId)
    {
        lock (sessionRouteSync)
        {
            if (!sessionRoutes.TryRemove(channel, out deviceId))
                return false;

            ChannelViewModel[] current = sessionRouteSnapshots[deviceId];
            if (current.Length == 1)
            {
                sessionRouteSnapshots.Remove(deviceId);
                return true;
            }

            var updated = new ChannelViewModel[current.Length - 1];
            int index = 0;
            foreach (ChannelViewModel candidate in current)
            {
                if (!ReferenceEquals(candidate, channel))
                    updated[index++] = candidate;
            }
            sessionRouteSnapshots[deviceId] = updated;
            return true;
        }
    }

    public void AddSessionPolicy(ChannelViewModel channel, bool followsSystemDefault)
        => sessionFollowsSystemDefault.Add(channel, followsSystemDefault);

    public void RemoveSessionPolicy(ChannelViewModel channel)
        => sessionFollowsSystemDefault.Remove(channel);

    public bool HasSessionsForRoute(string deviceId)
    {
        lock (sessionRouteSync)
            return sessionRouteSnapshots.ContainsKey(deviceId);
    }

    public ChannelViewModel[] GetSessionsForRoute(string deviceId)
    {
        lock (sessionRouteSync)
        {
            return sessionRouteSnapshots.TryGetValue(deviceId, out ChannelViewModel[]? snapshot)
                ? snapshot.ToArray()
                : [];
        }
    }

    // Called while the coordinator gate is held.
    public ChannelViewModel[] SelectSystemDefaultSessions(
        Func<ChannelViewModel, bool> isActive)
        => sessionFollowsSystemDefault
            .Where(pair => pair.Value && isActive(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();

    // Called while the coordinator gate is held.
    public ChannelViewModel[] ExpandSharedRouteSessions(ChannelViewModel[] requested)
    {
        if (requested.Length == 1)
        {
            lock (sessionRouteSync)
            {
                if (sessionRoutes.TryGetValue(requested[0], out string? requestedRouteId) &&
                    sessionRouteSnapshots.TryGetValue(requestedRouteId, out ChannelViewModel[]? snapshot))
                {
                    return snapshot.ToArray();
                }
            }
        }

        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelViewModel channel in requested)
        {
            if (sessionRoutes.TryGetValue(channel, out string? routeId))
                routeIds.Add(routeId);
        }
        if (routeIds.Count == 0)
            return requested;

        return sessionRoutes
            .Where(pair => routeIds.Contains(pair.Value))
            .Select(pair => pair.Key)
            .Concat(requested)
            .Distinct()
            .ToArray();
    }

    public void ClearSessions()
    {
        lock (sessionRouteSync)
        {
            sessionRoutes.Clear();
            sessionRouteSnapshots.Clear();
        }
        sessionFollowsSystemDefault.Clear();
    }

    public ReceiveAudioRoute[] RemoveAllRoutes()
    {
        ReceiveAudioRoute[] snapshot = routes.Values.ToArray();
        routes.Clear();
        return snapshot;
    }
}
