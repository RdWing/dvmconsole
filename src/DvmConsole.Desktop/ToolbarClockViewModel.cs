using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

public sealed record ToolbarClockColorOption(string Label, string ColorHex)
{
    public IBrush ColorBrush => new SolidColorBrush(Color.Parse(ColorHex));
}

public sealed class ToolbarClockViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<ToolbarClockColorOption> colorOptions =
    [
        new("Neutral", "#3A3A3A"),
        new("Blue", "#0D47A1"),
        new("Green", "#1B5E20"),
        new("Amber", "#B26A00"),
        new("Red", "#8E2424"),
        new("Purple", "#5E35B1"),
        new("Teal", "#00695C"),
        new("Slate", "#37474F")
    ];
    private bool enabled;
    private string utcOffsetText;
    private string timeText = string.Empty;
    private string colorHex = ToolbarClockColorPalette.DefaultColorHex;

    public ToolbarClockViewModel(int slotNumber, ToolbarClockSetting setting)
    {
        if (slotNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        ArgumentNullException.ThrowIfNull(setting);
        SlotNumber = slotNumber;
        enabled = setting.Enabled;
        utcOffsetText = setting.UtcOffsetHours.ToString(CultureInfo.InvariantCulture);
        colorHex = ToolbarClockColorPalette.Normalize(setting.ColorHex);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SlotNumber { get; }
    public string SlotLabel => $"Clock {SlotNumber}";

    public IReadOnlyList<ToolbarClockColorOption> ColorOptions => colorOptions;

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
                return;
            enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackgroundBrush)));
        }
    }

    public string UtcOffsetText
    {
        get => utcOffsetText;
        set => SetField(ref utcOffsetText, value ?? string.Empty);
    }

    public string TimeZoneLabel
        => TryGetUtcOffset(out int offsetHours)
            ? FormatUtcOffsetLabel(offsetHours)
            : "UTC?";

    public string TimeText => timeText;

    public string ColorHex
    {
        get => colorHex;
        set
        {
            string normalized = ToolbarClockColorPalette.Normalize(value);
            if (string.Equals(colorHex, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            colorHex = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorHex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedColorOption)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorBrush)));
        }
    }

    public ToolbarClockColorOption SelectedColorOption
    {
        get => colorOptions.First(option => option.ColorHex.Equals(ColorHex, StringComparison.OrdinalIgnoreCase));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ColorHex = value.ColorHex;
        }
    }

    public string ColorLabel
        => colorOptions.First(option => option.ColorHex.Equals(ColorHex, StringComparison.OrdinalIgnoreCase)).Label;

    public IBrush ColorBrush => new SolidColorBrush(Color.Parse(ColorHex));

    public IBrush BackgroundBrush => new SolidColorBrush(Color.Parse(
        Enabled ? ColorHex : "#1A222D"));

    public bool TryGetUtcOffset(out int offsetHours)
    {
        if (!int.TryParse(UtcOffsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out offsetHours))
        {
            offsetHours = 0;
            return false;
        }

        return offsetHours is >= -12 and <= 14;
    }

    public void Update(DateTimeOffset utcNow, bool use24HourTime, bool showSeconds)
    {
        if (!TryGetUtcOffset(out int offsetHours))
            return;
        DateTime time = utcNow.UtcDateTime.AddHours(offsetHours);
        string nextTime = MainWindowViewModel.FormatClock(time, use24HourTime, showSeconds);
        SetField(ref timeText, nextTime, nameof(TimeText));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeZoneLabel)));
    }

    public ToolbarClockSetting ToSetting()
        => new()
        {
            Enabled = Enabled,
            UtcOffsetHours = TryGetUtcOffset(out int offsetHours) ? offsetHours : 0,
            ColorHex = ColorHex
        };

    private static string FormatUtcOffsetLabel(int offsetHours)
        => $"UTC{(offsetHours >= 0 ? "+" : "-")}{Math.Abs(offsetHours):00}";

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(UtcOffsetText))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeZoneLabel)));
    }
}
