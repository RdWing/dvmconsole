using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace DvmConsole.Desktop;

internal static class ChannelCardInput
{
    public static bool IsInteractiveSource(object? source, Control card)
    {
        ArgumentNullException.ThrowIfNull(card);

        object? current = source;
        while (current is not null && !ReferenceEquals(current, card))
        {
            if (current is Button or Slider)
                return true;
            if (current is Control control && control.Classes.Contains("ptt-input-guard"))
                return true;

            current = current switch
            {
                Avalonia.Visual visual =>
                    visual.GetVisualParent() ?? (visual as ILogical)?.LogicalParent,
                ILogical logical => logical.LogicalParent,
                _ => null
            };
        }

        return false;
    }
}
