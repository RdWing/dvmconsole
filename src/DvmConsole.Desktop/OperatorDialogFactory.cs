using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DvmConsole.Desktop;

internal sealed record OperatorDialogParts(
    Window Window,
    ScrollViewer MessageScroller,
    TextBlock MessageText,
    Button PrimaryButton,
    Button? CancelButton = null,
    TextBox? Input = null);

internal sealed record OperatorDialogLayout(
    double Width,
    double MaxWidth,
    double MaxHeight,
    double MessageMaxHeight,
    SizeToContent SizeToContent,
    bool CanResize);

internal static class OperatorDialogFactory
{
    internal static OperatorDialogLayout Layout { get; } = new(520, 720, 600, 360, SizeToContent.Height, false);

    public static OperatorDialogParts CreateMessage(string title, string message, string closeLabel)
        => Create(title, message, closeLabel, includeCancel: false, inputWatermark: null);

    public static OperatorDialogParts CreateConfirmation(string title, string message, string confirmLabel)
        => Create(title, message, confirmLabel, includeCancel: true, inputWatermark: null);

    public static OperatorDialogParts CreateTextPrompt(
        string title,
        string message,
        string confirmLabel,
        string inputWatermark)
        => Create(title, message, confirmLabel, includeCancel: true, inputWatermark);

    private static OperatorDialogParts Create(
        string title,
        string message,
        string primaryLabel,
        bool includeCancel,
        string? inputWatermark)
    {
        var messageText = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        var messageScroller = new ScrollViewer
        {
            MaxHeight = Layout.MessageMaxHeight,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = messageText
        };
        TextBox? input = inputWatermark is null
            ? null
            : new TextBox { Watermark = inputWatermark, MinWidth = 320 };
        var primaryButton = new Button { Content = primaryLabel, MinWidth = 88 };
        Button? cancelButton = includeCancel ? new Button { Content = "Cancel", MinWidth = 88 } : null;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (cancelButton is not null)
            actions.Children.Add(cancelButton);
        actions.Children.Add(primaryButton);

        var content = new StackPanel { Margin = new Thickness(20), Spacing = 14 };
        content.Children.Add(messageScroller);
        if (input is not null)
            content.Children.Add(input);
        content.Children.Add(actions);

        var window = new Window
        {
            Title = title,
            Width = Layout.Width,
            MaxWidth = Layout.MaxWidth,
            MaxHeight = Layout.MaxHeight,
            SizeToContent = Layout.SizeToContent,
            CanResize = Layout.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        return new OperatorDialogParts(window, messageScroller, messageText, primaryButton, cancelButton, input);
    }
}
