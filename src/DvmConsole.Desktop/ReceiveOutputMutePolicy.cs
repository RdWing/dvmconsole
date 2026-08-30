namespace DvmConsole.Desktop;

// Owns operator-selected receive mute scopes. The coordinator remains
// responsible for the playback mechanism; this type only decides whether a
// channel's decoded PCM should currently reach live output.
internal sealed class ReceiveOutputMutePolicy
{
    private readonly HashSet<SystemViewModel> mutedSystems =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ZoneViewModel> mutedZones =
        new(ReferenceEqualityComparer.Instance);

    public bool IsMuted(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return mutedSystems.Any(system => system.Channels.Contains(channel)) ||
            mutedZones.Any(zone => zone.Channels.Contains(channel));
    }

    public bool ShouldEnableLivePlayback(
        ChannelViewModel channel,
        bool isTemporarilySuspended)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.IsAudioEnabled &&
            !isTemporarilySuspended &&
            !IsMuted(channel);
    }

    public bool IsMuted(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return mutedSystems.Contains(system);
    }

    public bool IsMuted(ZoneViewModel zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return mutedZones.Contains(zone);
    }

    public bool Toggle(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (mutedSystems.Remove(system))
            return false;
        mutedSystems.Add(system);
        return true;
    }

    public bool Toggle(ZoneViewModel zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (mutedZones.Remove(zone))
            return false;
        mutedZones.Add(zone);
        return true;
    }
}
