// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;

namespace DvmConsole.Application;

// Keeps the common one-channel receive dispatch allocation-free while still
// representing destinationless terminators that close multiple decoders.
internal readonly struct ReceiveStateTargets : IReadOnlyList<ConsoleChannelState>
{
    private readonly ConsoleChannelState? single;
    private readonly ConsoleChannelState[]? multiple;

    private ReceiveStateTargets(ConsoleChannelState single)
        => this.single = single;

    private ReceiveStateTargets(ConsoleChannelState[] multiple)
        => this.multiple = multiple;

    public static ReceiveStateTargets Empty => default;

    public int Count => multiple?.Length ?? (single is null ? 0 : 1);

    public ConsoleChannelState this[int index]
    {
        get
        {
            if (multiple is not null)
                return multiple[index];
            if (index == 0 && single is not null)
                return single;
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public static ReceiveStateTargets One(ConsoleChannelState channel)
        => new(channel ?? throw new ArgumentNullException(nameof(channel)));

    public static ReceiveStateTargets FromArray(ConsoleChannelState[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels.Length switch
        {
            0 => Empty,
            1 => One(channels[0]),
            _ => new ReceiveStateTargets(channels)
        };
    }

    public static ReceiveStateTargets From(IReadOnlyList<ConsoleChannelState>? channels)
    {
        if (channels is null || channels.Count == 0)
            return Empty;
        if (channels.Count == 1)
            return One(channels[0]);

        var copy = new ConsoleChannelState[channels.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = channels[index];
        return new ReceiveStateTargets(copy);
    }

    public bool Contains(ConsoleChannelState channel)
    {
        if (ReferenceEquals(single, channel))
            return true;
        if (multiple is null)
            return false;
        for (int index = 0; index < multiple.Length; index++)
        {
            if (ReferenceEquals(multiple[index], channel))
                return true;
        }
        return false;
    }

    public ConsoleChannelState[] ToArray()
    {
        if (multiple is not null)
            return multiple.ToArray();
        return single is null ? [] : [single];
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<ConsoleChannelState> IEnumerable<ConsoleChannelState>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal struct Enumerator(ReceiveStateTargets targets) : IEnumerator<ConsoleChannelState>
    {
        private int index = -1;

        public ConsoleChannelState Current => targets[index];
        object IEnumerator.Current => Current;

        public bool MoveNext() => ++index < targets.Count;
        public void Reset() => index = -1;
        public void Dispose() { }
    }
}
