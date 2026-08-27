using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

// Keeps construction policy at the composition boundary while the view model
// remains the stable facade consumed by Avalonia bindings and tests.
internal sealed record MainWindowViewModelOptions(
    IP25KeyResolver? P25KeyResolver = null,
    UserSettingsStore? UserSettingsStore = null,
    IEnumerable<GroupConfiguration>? GroupDefinitions = null,
    bool PatchSourceIdPassthrough = false,
    Func<IReadOnlyList<string>>? SerialPortProvider = null,
    Func<string, int, IPttSource>? SerialPttFactory = null,
    IDmrKeyResolver? DmrKeyResolver = null,
    INxdnKeyResolver? NxdnKeyResolver = null,
    string? CodeplugPath = null,
    IUiDispatcher? UiDispatcher = null,
    ConsoleSessionServices? SessionServices = null,
    bool NetworkDisabledDemo = false,
    Func<ApplicationAudioConfiguration, Task>? ReconfigureApplicationAudio = null);
