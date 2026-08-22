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
    private readonly ConcurrentDictionary<ChannelViewModel, string> sessionRoutes = [];
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
        => sessionRoutes.TryAdd(channel, deviceId);

    public bool TryRemoveSessionRoute(ChannelViewModel channel, out string? deviceId)
        => sessionRoutes.TryRemove(channel, out deviceId);

    public void AddSessionPolicy(ChannelViewModel channel, bool followsSystemDefault)
        => sessionFollowsSystemDefault.Add(channel, followsSystemDefault);

    public void RemoveSessionPolicy(ChannelViewModel channel)
        => sessionFollowsSystemDefault.Remove(channel);

    public bool HasSessionsForRoute(string deviceId)
        => sessionRoutes.Values.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

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
        sessionRoutes.Clear();
        sessionFollowsSystemDefault.Clear();
    }

    public ReceiveAudioRoute[] RemoveAllRoutes()
    {
        ReceiveAudioRoute[] snapshot = routes.Values.ToArray();
        routes.Clear();
        return snapshot;
    }
}
