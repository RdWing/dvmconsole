using System.Collections;
using System.Windows.Input;

namespace DvmConsole.Presentation;

public interface IDtmfPresetViewModel
{
    string DisplayText { get; }
}

public interface ITonePresetViewModel
{
    string Name { get; }
    string DisplayText { get; }
}

public interface IToneSequenceStepViewModel
{
    bool IsSilence { get; set; }
    string FrequencyText { get; set; }
    string DurationText { get; set; }
}

public interface IAlertToneViewModel
{
    string Name { get; }
    string DisplayText { get; }
    string StorageText { get; }
}

public interface IToneSettingsViewModel
{
    string DtmfDigits { get; set; }
    string DtmfPresetName { get; set; }
    ICommand SendDtmfCommand { get; }
    ICommand SaveDtmfPresetCommand { get; }
    IEnumerable DtmfPresets { get; }
    IEnumerable ToneSequenceSteps { get; }
    string TonePresetName { get; set; }
    ICommand SendToneCommand { get; }
    ICommand SaveTonePresetCommand { get; }
    IEnumerable TonePresets { get; }
    string QuickCallToneAText { get; set; }
    string QuickCallToneBText { get; set; }
    string AlertToneNameText { get; set; }
    IEnumerable AlertTones { get; }
}

public sealed class DtmfPresetEventArgs(IDtmfPresetViewModel preset) : EventArgs
{
    public IDtmfPresetViewModel Preset { get; } = preset ?? throw new ArgumentNullException(nameof(preset));
}

public sealed class TonePresetEventArgs(ITonePresetViewModel preset) : EventArgs
{
    public ITonePresetViewModel Preset { get; } = preset ?? throw new ArgumentNullException(nameof(preset));
}

public sealed class ToneSequenceStepEventArgs(IToneSequenceStepViewModel step) : EventArgs
{
    public IToneSequenceStepViewModel Step { get; } = step ?? throw new ArgumentNullException(nameof(step));
}

public sealed class AlertToneEventArgs(IAlertToneViewModel tone) : EventArgs
{
    public IAlertToneViewModel Tone { get; } = tone ?? throw new ArgumentNullException(nameof(tone));
}
