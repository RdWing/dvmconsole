using DvmConsole.Core.Settings;
using System.ComponentModel;

namespace DvmConsole.Desktop;

internal enum RxJitterBufferProtocol
{
    P25,
    Dmr,
    Nxdn
}

public sealed record RxJitterBufferOption(
    int Milliseconds,
    bool IsAdaptive,
    string Label);

public sealed class RxJitterBufferModeViewModel : INotifyPropertyChanged
{
    private RxJitterBufferOption selectedOption;
    private int lastFixedMilliseconds;

    internal RxJitterBufferModeViewModel(
        RxJitterBufferProtocol protocol,
        string modeName,
        IReadOnlyList<int> allowedMilliseconds,
        int selectedMilliseconds,
        bool adaptive,
        int packetMilliseconds,
        string singularUnit = "packet",
        string pluralUnit = "packets")
    {
        Protocol = protocol;
        ModeName = modeName;
        Options = allowedMilliseconds
            .Select(value => new RxJitterBufferOption(
                value,
                IsAdaptive: false,
                CreateLabel(value, packetMilliseconds, singularUnit, pluralUnit)))
            .Append(new RxJitterBufferOption(
                allowedMilliseconds[^1],
                IsAdaptive: true,
                CreateAdaptiveLabel(allowedMilliseconds[^1])))
            .ToArray();
        lastFixedMilliseconds = selectedMilliseconds;
        selectedOption = adaptive
            ? Options.Single(option => option.IsAdaptive)
            : Options.First(option => !option.IsAdaptive && option.Milliseconds == selectedMilliseconds);
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
            if (!value.IsAdaptive)
                lastFixedMilliseconds = value.Milliseconds;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOption)));
        }
    }

    internal int FixedMilliseconds => lastFixedMilliseconds;
    internal bool IsAdaptive => SelectedOption.IsAdaptive;
    internal RxJitterBufferProtocol Protocol { get; }
    internal string SummaryText => IsAdaptive
        ? $"adaptive ≤ {SelectedOption.Milliseconds} ms"
        : $"{SelectedOption.Milliseconds} ms";

    internal void Restore(int milliseconds, bool adaptive)
    {
        lastFixedMilliseconds = milliseconds;
        SelectedOption = adaptive
            ? Options.Single(option => option.IsAdaptive)
            : Options.First(option => !option.IsAdaptive && option.Milliseconds == milliseconds);
    }

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

    private static string CreateAdaptiveLabel(int maximumMilliseconds)
        => $"Adaptive ≤ {maximumMilliseconds} ms";
}
