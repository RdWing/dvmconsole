// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

public sealed record ConsoleReceiveBufferingOptions(
    int P25Milliseconds = RxJitterBufferSetting.DefaultP25Milliseconds, bool P25Adaptive = true,
    int DmrMilliseconds = RxJitterBufferSetting.DefaultDmrMilliseconds, bool DmrAdaptive = true,
    int NxdnMilliseconds = RxJitterBufferSetting.DefaultNxdnMilliseconds, bool NxdnAdaptive = true)
{
    public static ConsoleReceiveBufferingOptions Default { get; } = new();

    public void Validate()
    {
        if (!RxJitterBufferSetting.P25OptionsMilliseconds.Contains(P25Milliseconds))
            throw new ArgumentOutOfRangeException(nameof(P25Milliseconds));
        if (!RxJitterBufferSetting.DmrOptionsMilliseconds.Contains(DmrMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(DmrMilliseconds));
        if (!RxJitterBufferSetting.NxdnOptionsMilliseconds.Contains(NxdnMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(NxdnMilliseconds));
    }

    public static ConsoleReceiveBufferingOptions FromSetting(RxJitterBufferSetting? setting)
    {
        var normalized = RxJitterBufferSetting.Normalize(setting);
        return new(normalized.P25Milliseconds, normalized.P25Adaptive, normalized.DmrMilliseconds,
            normalized.DmrAdaptive, normalized.NxdnMilliseconds, normalized.NxdnAdaptive);
    }

    public RxJitterBufferSetting ToSetting()
    {
        Validate();
        return new()
        {
            P25Milliseconds = P25Milliseconds,
            P25Adaptive = P25Adaptive,
            DmrMilliseconds = DmrMilliseconds,
            DmrAdaptive = DmrAdaptive,
            NxdnMilliseconds = NxdnMilliseconds,
            NxdnAdaptive = NxdnAdaptive
        };
    }
}

public interface IConsoleReceiveBufferingSettings
{
    ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions> ReceiveBuffering { get; }
    bool CanSaveReceiveBuffering { get; }
    ValueTask SetReceiveBufferingAsync(SystemId system, ConsoleReceiveBufferingOptions options, CancellationToken cancellationToken = default);
}

public interface IConsoleReceiveBufferingStore
{
    ValueTask<ImmutableDictionary<string, ConsoleReceiveBufferingOptions>> LoadReceiveBufferingAsync(IReadOnlyList<string> systems, CancellationToken cancellationToken = default);
    ValueTask SaveReceiveBufferingAsync(string system, ConsoleReceiveBufferingOptions options, CancellationToken cancellationToken = default);
}
