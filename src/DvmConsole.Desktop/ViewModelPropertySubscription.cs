using System.ComponentModel;

namespace DvmConsole.Desktop;

// Owns the shell's single active view-model subscription. Keeping this tiny
// lifecycle concern outside MainWindow makes reload ordering explicit and
// prevents handlers from remaining attached to a disposed replacement.
internal sealed class ViewModelPropertySubscription<T> : IDisposable
    where T : INotifyPropertyChanged
{
    private readonly PropertyChangedEventHandler handler;
    private T? current;

    public ViewModelPropertySubscription(T initial, PropertyChangedEventHandler handler)
    {
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Rebind(initial);
    }

    public void Rebind(T replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(current, replacement))
            return;

        if (current is not null)
            current.PropertyChanged -= handler;
        current = replacement;
        current.PropertyChanged += handler;
    }

    public void Dispose()
    {
        if (current is not null)
            current.PropertyChanged -= handler;
        current = default;
    }
}
