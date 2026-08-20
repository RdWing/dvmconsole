using DvmConsole.Core.Settings;
using DvmConsole.Vocoder;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

public sealed class RxAudioProcessingModeViewModel : INotifyPropertyChanged
{
    private bool highPassFilterEnabled;
    private decimal highPassFrequencyHz;
    private bool peakingFilterEnabled;
    private decimal peakingFrequencyHz;
    private decimal peakingGainDb;
    private bool compressorEnabled;
    private decimal compressorRatio;
    private decimal compressorThresholdDbfs;
    private decimal compressorMakeupGainDb;

    internal RxAudioProcessingModeViewModel(
        string settingsKey,
        string modeName,
        VocoderMode vocoderMode,
        RxAudioProcessingModeSetting setting)
    {
        SettingsKey = settingsKey;
        ModeName = modeName;
        VocoderMode = vocoderMode;
        Restore(setting);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModeName { get; }

    public bool HighPassFilterEnabled
    {
        get => highPassFilterEnabled;
        set => SetField(ref highPassFilterEnabled, value);
    }

    public decimal HighPassFrequencyHz
    {
        get => highPassFrequencyHz;
        set => SetField(ref highPassFrequencyHz, value);
    }

    public bool PeakingFilterEnabled
    {
        get => peakingFilterEnabled;
        set => SetField(ref peakingFilterEnabled, value);
    }

    public decimal PeakingFrequencyHz
    {
        get => peakingFrequencyHz;
        set => SetField(ref peakingFrequencyHz, value);
    }

    public decimal PeakingGainDb
    {
        get => peakingGainDb;
        set => SetField(ref peakingGainDb, value);
    }

    public bool CompressorEnabled
    {
        get => compressorEnabled;
        set => SetField(ref compressorEnabled, value);
    }

    public decimal CompressorRatio
    {
        get => compressorRatio;
        set => SetField(ref compressorRatio, value);
    }

    public decimal CompressorThresholdDbfs
    {
        get => compressorThresholdDbfs;
        set => SetField(ref compressorThresholdDbfs, value);
    }

    public decimal CompressorMakeupGainDb
    {
        get => compressorMakeupGainDb;
        set => SetField(ref compressorMakeupGainDb, value);
    }

    internal string SettingsKey { get; }
    internal VocoderMode VocoderMode { get; }

    internal void Restore(RxAudioProcessingModeSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        HighPassFilterEnabled = setting.HighPassFilterEnabled;
        HighPassFrequencyHz = (decimal)setting.HighPassFrequencyHz;
        PeakingFilterEnabled = setting.PeakingFilterEnabled;
        PeakingFrequencyHz = (decimal)setting.PeakingFrequencyHz;
        PeakingGainDb = (decimal)setting.PeakingGainDb;
        CompressorEnabled = setting.CompressorEnabled;
        CompressorRatio = (decimal)setting.CompressorRatio;
        CompressorThresholdDbfs = (decimal)setting.CompressorThresholdDbfs;
        CompressorMakeupGainDb = (decimal)setting.CompressorMakeupGainDb;
    }

    internal RxAudioProcessingModeSetting ToSetting()
        => new()
        {
            HighPassFilterEnabled = HighPassFilterEnabled,
            HighPassFrequencyHz = (double)HighPassFrequencyHz,
            PeakingFilterEnabled = PeakingFilterEnabled,
            PeakingFrequencyHz = (double)PeakingFrequencyHz,
            PeakingGainDb = (double)PeakingGainDb,
            CompressorEnabled = CompressorEnabled,
            CompressorRatio = (double)CompressorRatio,
            CompressorThresholdDbfs = (double)CompressorThresholdDbfs,
            CompressorMakeupGainDb = (double)CompressorMakeupGainDb
        };

    internal ReceiveAudioProcessingOptions ToVocoderOptions()
        => new()
        {
            HighPassFilterEnabled = HighPassFilterEnabled,
            HighPassFrequencyHz = (float)HighPassFrequencyHz,
            PeakingFilterEnabled = PeakingFilterEnabled,
            PeakingFrequencyHz = (float)PeakingFrequencyHz,
            PeakingGainDb = (float)PeakingGainDb,
            CompressorEnabled = CompressorEnabled,
            CompressorRatio = (float)CompressorRatio,
            CompressorThresholdDbfs = (float)CompressorThresholdDbfs,
            CompressorMakeupGainDb = (float)CompressorMakeupGainDb
        };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
