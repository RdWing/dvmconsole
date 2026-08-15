using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

public sealed class ToolbarClockViewModel : INotifyPropertyChanged
{
    private bool enabled;
    private string utcOffsetText;
    private string timeText = string.Empty;

    public ToolbarClockViewModel(int slotNumber, ToolbarClockSetting setting)
    {
        if (slotNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        ArgumentNullException.ThrowIfNull(setting);
        SlotNumber = slotNumber;
        enabled = setting.Enabled;
        utcOffsetText = setting.UtcOffsetHours.ToString(CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SlotNumber { get; }
    public string SlotLabel => $"Clock {SlotNumber}";

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

    public IBrush BackgroundBrush => new SolidColorBrush(Color.Parse(
        Enabled ? "#243B53" : "#1A222D"));

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
            UtcOffsetHours = TryGetUtcOffset(out int offsetHours) ? offsetHours : 0
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
