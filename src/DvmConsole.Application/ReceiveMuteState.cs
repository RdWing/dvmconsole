// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;

namespace DvmConsole.Application;

public enum ReceiveMuteScopeKind { System, Zone }

/// <summary>
/// A scope identity is local to a session. Distinct system-specific copies of
/// the same named zone must retain separate identities and memberships.
/// </summary>
public sealed class ReceiveMuteScope(
    ReceiveMuteScopeKind kind, string name, IEnumerable<ChannelId> channels)
{
    public Guid Id { get; } = Guid.NewGuid();
    public ReceiveMuteScopeKind Kind { get; } = kind;
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
    public ImmutableHashSet<ChannelId> Channels { get; } = channels.ToImmutableHashSet();
}

/// <summary>Owns operator mute intent independently of physical playback routes.</summary>
public sealed class ReceiveMuteState
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, ReceiveMuteScope> muted = [];
    private bool globallyMuted;
    public bool GloballyMuted => Volatile.Read(ref globallyMuted);
    public event EventHandler? Changed;

    public void SetGlobalMuted(bool value)
    {
        lock (sync)
        {
            if (globallyMuted == value) return;
            Volatile.Write(ref globallyMuted, value);
        }
        PublishChanged();
    }

    public bool Toggle(ReceiveMuteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        bool enabled;
        lock (sync)
        {
            enabled = !muted.Remove(scope.Id);
            if (enabled) muted.Add(scope.Id, scope);
        }
        PublishChanged();
        return enabled;
    }

    private void PublishChanged()
    {
        foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Operator state does not depend on presentation observers. */ }
        }
    }

    public bool IsMuted(ReceiveMuteScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (sync) return muted.ContainsKey(scope.Id);
    }

    public bool IsMuted(ChannelId channel)
    {
        lock (sync) return muted.Values.Any(scope => scope.Channels.Contains(channel));
    }

    public bool ShouldEnableLivePlayback(ChannelId channel, bool selected, bool suspended)
        => selected && !suspended && !IsMuted(channel);

    public string? GetEffectiveReason(ChannelId channel, bool globallyMuted)
    {
        if (globallyMuted) return "global output mute";
        lock (sync)
        {
            ReceiveMuteScope? system = muted.Values.FirstOrDefault(scope =>
                scope.Kind == ReceiveMuteScopeKind.System && scope.Channels.Contains(channel));
            if (system is not null) return $"system {system.Name} output mute";
            ReceiveMuteScope? zone = muted.Values.FirstOrDefault(scope =>
                scope.Kind == ReceiveMuteScopeKind.Zone && scope.Channels.Contains(channel));
            return zone is null ? null : $"zone {zone.Name} output mute";
        }
    }
}
