// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DvmConsole.Mobile;

/// <summary>Product identity and attribution, using the console's shared theme.</summary>
internal sealed class MobileAboutView : UserControl
{
    // The small brand image is shared across settings sessions, like other app resources.
    private static readonly Lazy<Bitmap> Mark = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://DvmConsole.Mobile/Assets/NeoMark.png"));
        return Bitmap.DecodeToWidth(stream, 192);
    });

    public MobileAboutView()
    {
        var body = new StackPanel { Spacing = 16 };
        var identity = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 16 };
        var image = new Image
        {
            Source = Mark.Value,
            Width = 80,
            Height = 80,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(image, "DVM Console NEO logo");
        identity.Children.Add(image);
        var name = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Label("DVM Console", 17, FontWeight.SemiBold));
        var neo = Label("NEO", 34, FontWeight.Bold);
        neo.Bind(TextBlock.ForegroundProperty, neo.GetResourceObservable("BrandAccentBrush"));
        name.Children.Add(neo);
        Grid.SetColumn(name, 1);
        identity.Children.Add(name);
        var introduction = new StackPanel { Spacing = 12 };
        introduction.Children.Add(identity);
        introduction.Children.Add(Label("Built for busy systems.", 17, FontWeight.SemiBold));
        introduction.Children.Add(Label("Version " +
            (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown"), 13));
        body.Children.Add(Panel(introduction));

        var information = new StackPanel { Spacing = 10 };
        information.Children.Add(Label("Many channels. One console.", 17, FontWeight.SemiBold));
        information.Children.Add(Label("An open-source radio console for monitoring, transmitting, and recording across your DVM FNE networks.", 15));
        body.Children.Add(Panel(information));

        var safety = Panel(Label("For amateur and educational use. Not for public- or life-safety operation.", 14, FontWeight.SemiBold));
        safety.Bind(Border.BackgroundProperty, safety.GetResourceObservable("WarningBackgroundBrush"));
        safety.Bind(Border.BorderBrushProperty, safety.GetResourceObservable("WarningBorderBrush"));
        body.Children.Add(safety);

        var attribution = new StackPanel { Spacing = 12 };
        attribution.Children.Add(Label("Open source", 17, FontWeight.SemiBold));
        attribution.Children.Add(Label("Copyright © 2025–2026 RdWing.\nLicensed under GNU AGPL-3.0-only. Distributed without warranty.", 14));
        attribution.Children.Add(Link("Repository and licenses", "https://github.com/RdWing/dvmconsole"));
        attribution.Children.Add(Link("Upstream project", "https://github.com/DVMProject/dvmconsole"));
        attribution.Children.Add(Label("Independently maintained downstream of DVMProject/dvmconsole. NEO is not an official DVMProject release and does not imply DVMProject endorsement.", 13));
        body.Children.Add(Panel(attribution));
        Content = body;
    }

    private static TextBlock Label(string text, double size, FontWeight? weight = null) =>
        new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = weight ?? FontWeight.Normal
        }.WithScaledFontSize(size);

    private static Border Panel(Control content)
    {
        var panel = new Border
        {
            Child = content,
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1)
        };
        panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("CardBackgroundBrush"));
        panel.Bind(Border.BorderBrushProperty, panel.GetResourceObservable("ControlBorderBrush"));
        return panel;
    }

    private Button Link(string title, string address)
    {
        var button = new Button
        {
            Content = title + "  ↗",
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(button, title + ", opens in browser");
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(address));
        };
        return button;
    }
}
