using DvmConsole.Core.Settings;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

internal enum RxJitterBufferMode
{
    P25,
    Dmr,
    Nxdn
}

public sealed record RxJitterBufferOption(int Milliseconds, string Label);

public sealed class RxJitterBufferModeViewModel : INotifyPropertyChanged
{
    private RxJitterBufferOption selectedOption;

    internal RxJitterBufferModeViewModel(
        RxJitterBufferMode mode,
        string modeName,
        IReadOnlyList<int> allowedMilliseconds,
        int selectedMilliseconds,
        int packetMilliseconds,
        string singularUnit = "packet",
        string pluralUnit = "packets")
    {
        Mode = mode;
        ModeName = modeName;
        Options = allowedMilliseconds
            .Select(value => new RxJitterBufferOption(
                value,
                CreateLabel(value, packetMilliseconds, singularUnit, pluralUnit)))
            .ToArray();
        selectedOption = Options.First(option => option.Milliseconds == selectedMilliseconds);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModeName { get; }
    public IReadOnlyList<RxJitterBufferOption> Options { get; }

    public RxJitterBufferOption SelectedOption
    {
        get => selectedOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (selectedOption == value)
                return;
            selectedOption = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOption)));
        }
    }

    internal int Milliseconds => SelectedOption.Milliseconds;
    internal RxJitterBufferMode Mode { get; }

    internal void Restore(int milliseconds)
        => SelectedOption = Options.First(option => option.Milliseconds == milliseconds);

    private static string CreateLabel(
        int milliseconds,
        int packetMilliseconds,
        string singularUnit,
        string pluralUnit)
    {
        if (milliseconds == 0)
            return "Off (lowest latency)";

        int packetCount = milliseconds / packetMilliseconds;
        string unit = packetCount == 1 ? singularUnit : pluralUnit;
        return $"{milliseconds} ms ({packetCount} {unit})";
    }
}
