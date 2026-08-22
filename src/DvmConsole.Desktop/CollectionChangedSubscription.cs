using System.Collections.Specialized;

namespace DvmConsole.Desktop;

// Owns one replaceable collection subscription. The shell swaps its complete
// session model at runtime, so collection handlers must follow the active
// model instead of remaining attached to the initial instance.
internal sealed class CollectionChangedSubscription : IDisposable
{
    private readonly NotifyCollectionChangedEventHandler handler;
    private INotifyCollectionChanged? current;

    public CollectionChangedSubscription(
        INotifyCollectionChanged initial,
        NotifyCollectionChangedEventHandler handler)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Rebind(initial);
    }

    public void Rebind(INotifyCollectionChanged replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(current, replacement))
            return;

        if (current is not null)
            current.CollectionChanged -= handler;
        current = replacement;
        current.CollectionChanged += handler;
    }

    public void Dispose()
    {
        if (current is not null)
            current.CollectionChanged -= handler;
        current = null;
    }
}
