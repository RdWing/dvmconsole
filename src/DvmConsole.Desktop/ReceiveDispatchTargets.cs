using System.Collections;

namespace DvmConsole.Desktop;

// Keeps the common one-channel receive dispatch allocation-free while still
// representing destinationless terminators that close multiple decoders.
internal readonly struct ReceiveDispatchTargets : IReadOnlyList<ChannelViewModel>
{
    private readonly ChannelViewModel? single;
    private readonly ChannelViewModel[]? multiple;

    private ReceiveDispatchTargets(ChannelViewModel single)
        => this.single = single;

    private ReceiveDispatchTargets(ChannelViewModel[] multiple)
        => this.multiple = multiple;

    public static ReceiveDispatchTargets Empty => default;

    public int Count => multiple?.Length ?? (single is null ? 0 : 1);

    public ChannelViewModel this[int index]
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

    public static ReceiveDispatchTargets One(ChannelViewModel channel)
        => new(channel ?? throw new ArgumentNullException(nameof(channel)));

    public static ReceiveDispatchTargets FromArray(ChannelViewModel[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels.Length switch
        {
            0 => Empty,
            1 => One(channels[0]),
            _ => new ReceiveDispatchTargets(channels)
        };
    }

    public static ReceiveDispatchTargets From(IReadOnlyList<ChannelViewModel>? channels)
    {
        if (channels is null || channels.Count == 0)
            return Empty;
        if (channels.Count == 1)
            return One(channels[0]);

        var copy = new ChannelViewModel[channels.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = channels[index];
        return new ReceiveDispatchTargets(copy);
    }

    public bool Contains(ChannelViewModel channel)
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

    public ChannelViewModel[] ToArray()
    {
        if (multiple is not null)
            return multiple.ToArray();
        return single is null ? [] : [single];
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<ChannelViewModel> IEnumerable<ChannelViewModel>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal struct Enumerator(ReceiveDispatchTargets targets) : IEnumerator<ChannelViewModel>
    {
        private int index = -1;

        public ChannelViewModel Current => targets[index];
        object IEnumerator.Current => Current;

        public bool MoveNext() => ++index < targets.Count;
        public void Reset() => index = -1;
        public void Dispose() { }
    }
}
