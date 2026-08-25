using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DvmConsole.Desktop;

public sealed partial class AdaptiveSettingsSection : UserControl
{
    public static readonly StyledProperty<string> HeaderProperty =
        AvaloniaProperty.Register<AdaptiveSettingsSection, string>(nameof(Header), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<AdaptiveSettingsSection, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<object?> SectionContentProperty =
        AvaloniaProperty.Register<AdaptiveSettingsSection, object?>(nameof(SectionContent));

    public AdaptiveSettingsSection()
        => AvaloniaXamlLoader.Load(this);

    public string Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }

}
