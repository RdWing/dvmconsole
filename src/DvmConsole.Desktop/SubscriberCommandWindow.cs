using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal sealed record SubscriberCommandWindowLayout(
    SizeToContent SizeToContent,
    double MaxHeight,
    bool CanResize);

public sealed class SubscriberCommandWindow : Window
{
    internal static SubscriberCommandWindowLayout Layout { get; } =
        new(SizeToContent.Height, 440, false);

    private readonly MainWindowViewModel viewModel;
    private readonly P25SubscriberCommand command;
    private readonly ComboBox systemSelector;
    private readonly TextBox destinationInput;
    private readonly TextBlock statusText;

    public SubscriberCommandWindow(MainWindowViewModel viewModel, P25SubscriberCommand command)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.command = command;
        Title = CommandTitle(command);
        Width = 520;
        SizeToContent = Layout.SizeToContent;
        MaxHeight = Layout.MaxHeight;
        CanResize = Layout.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        systemSelector = new ComboBox
        {
            ItemsSource = viewModel.Systems,
            SelectedItem = viewModel.SelectedSystem,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<SystemViewModel>(
                (system, _) => new TextBlock { Text = $"{system.Name} — {system.ConnectionStatus}" })
        };
        destinationInput = new TextBox { Watermark = "P25 subscriber RID (1–16777215)" };
        statusText = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var sendButton = new Button { Content = CommandTitle(command), MinWidth = 110 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };

        var body = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = CommandDescription(command), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "FNE system" },
                systemSelector,
                new TextBlock { Text = "Destination subscriber" },
                destinationInput
            }
        };

        if (command is P25SubscriberCommand.Inhibit or P25SubscriberCommand.Uninhibit)
        {
            sendButton.IsEnabled = false;
            var acknowledgement = new CheckBox
            {
                Content = command == P25SubscriberCommand.Inhibit
                    ? "I understand that inhibit can disable the target subscriber."
                    : "I confirm that this is the intended target subscriber."
            };
            acknowledgement.IsCheckedChanged += (_, _) => sendButton.IsEnabled = acknowledgement.IsChecked == true;
            body.Children.Add(acknowledgement);
        }

        body.Children.Add(statusText);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, sendButton }
        });
        Content = body;

        cancelButton.Click += (_, _) => Close();
        sendButton.Click += HandleSendClick;
        Opened += (_, _) => destinationInput.Focus();
    }

    private void HandleSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (systemSelector.SelectedItem is not SystemViewModel system)
        {
            statusText.Text = "Select an FNE system.";
            return;
        }

        if (!viewModel.TrySendSubscriberCommand(system, command, destinationInput.Text, out string message))
        {
            statusText.Text = message;
            return;
        }

        Close();
    }

    private static string CommandTitle(P25SubscriberCommand command) => command switch
    {
        P25SubscriberCommand.CallAlert => "Page Subscriber",
        P25SubscriberCommand.RadioCheck => "Radio Check Subscriber",
        P25SubscriberCommand.Inhibit => "Inhibit Subscriber",
        P25SubscriberCommand.Uninhibit => "Uninhibit Subscriber",
        _ => command.ToString()
    };

    private static string CommandDescription(P25SubscriberCommand command) => command switch
    {
        P25SubscriberCommand.CallAlert => "Send a P25 call-alert page to one subscriber through the selected connected FNE system.",
        P25SubscriberCommand.RadioCheck => "Send a P25 radio-check request. Acknowledgement decoding remains future work.",
        P25SubscriberCommand.Inhibit => "Send a P25 inhibit command. Verify the destination RID carefully before continuing.",
        P25SubscriberCommand.Uninhibit => "Send a P25 uninhibit command to a previously inhibited subscriber.",
        _ => string.Empty
    };
}
