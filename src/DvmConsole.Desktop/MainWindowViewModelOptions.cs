// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Media;
using DvmConsole.Ptt;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

internal sealed record MainWindowSecurityOptions(
    IP25KeyResolver? P25KeyResolver = null,
    IDmrKeyResolver? DmrKeyResolver = null,
    INxdnKeyResolver? NxdnKeyResolver = null);

internal sealed record MainWindowDocumentOptions(
    UserSettingsStore? UserSettingsStore = null,
    IAssetStore? AssetStore = null,
    string? CodeplugPath = null,
    ConfigurationReference? ConfigurationReference = null,
    bool MigrateLegacyConfigurationOperatorState = false);

internal sealed record MainWindowHostOptions(
    Func<IReadOnlyList<string>>? SerialPortProvider = null,
    Func<string, int, IPttSource>? SerialPttFactory = null,
    IUiDispatcher? UiDispatcher = null,
    ConsoleSessionServices? SessionServices = null,
    Func<ApplicationAudioConfiguration, Task>? ReconfigureApplicationAudio = null,
    IDesktopPrivacyPermissionService? PrivacyPermissionService = null,
    IAudioBackendFactory? AudioBackendFactory = null,
    IVocoderFactory? VocoderFactory = null,
    MainWindowSessionComposition? SessionComposition = null);

internal sealed record MainWindowFeatureOptions(
    IEnumerable<GroupConfiguration>? GroupDefinitions = null,
    bool PatchSourceIdPassthrough = false,
    bool NetworkDisabledDemo = false);

// Four cohesive groups keep construction policy explicit without making the
// view-model constructor depend on a long, order-sensitive parameter bag.
internal sealed record MainWindowViewModelOptions(
    MainWindowSecurityOptions? Security = null,
    MainWindowDocumentOptions? Document = null,
    MainWindowHostOptions? Host = null,
    MainWindowFeatureOptions? Features = null)
{
    private static readonly MainWindowSecurityOptions EmptySecurity = new();
    private static readonly MainWindowDocumentOptions EmptyDocument = new();
    private static readonly MainWindowHostOptions EmptyHost = new();
    private static readonly MainWindowFeatureOptions EmptyFeatures = new();
    private MainWindowSecurityOptions EffectiveSecurity => Security ?? EmptySecurity;
    private MainWindowDocumentOptions EffectiveDocument => Document ?? EmptyDocument;
    private MainWindowHostOptions EffectiveHost => Host ?? EmptyHost;
    private MainWindowFeatureOptions EffectiveFeatures => Features ?? EmptyFeatures;

    public IP25KeyResolver? P25KeyResolver => EffectiveSecurity.P25KeyResolver;
    public IDmrKeyResolver? DmrKeyResolver => EffectiveSecurity.DmrKeyResolver;
    public INxdnKeyResolver? NxdnKeyResolver => EffectiveSecurity.NxdnKeyResolver;
    public UserSettingsStore? UserSettingsStore => EffectiveDocument.UserSettingsStore;
    public IAssetStore? AssetStore => EffectiveDocument.AssetStore;
    public string? CodeplugPath => EffectiveDocument.CodeplugPath;
    public ConfigurationReference? ConfigurationReference => EffectiveDocument.ConfigurationReference;
    public bool MigrateLegacyConfigurationOperatorState => EffectiveDocument.MigrateLegacyConfigurationOperatorState;
    public Func<IReadOnlyList<string>>? SerialPortProvider => EffectiveHost.SerialPortProvider;
    public Func<string, int, IPttSource>? SerialPttFactory => EffectiveHost.SerialPttFactory;
    public IUiDispatcher? UiDispatcher => EffectiveHost.UiDispatcher;
    public ConsoleSessionServices? SessionServices => EffectiveHost.SessionServices;
    public Func<ApplicationAudioConfiguration, Task>? ReconfigureApplicationAudio => EffectiveHost.ReconfigureApplicationAudio;
    public IDesktopPrivacyPermissionService? PrivacyPermissionService => EffectiveHost.PrivacyPermissionService;
    public IAudioBackendFactory? AudioBackendFactory => EffectiveHost.AudioBackendFactory;
    public IVocoderFactory? VocoderFactory => EffectiveHost.VocoderFactory;
    public MainWindowSessionComposition? SessionComposition => EffectiveHost.SessionComposition;
    public IEnumerable<GroupConfiguration>? GroupDefinitions => EffectiveFeatures.GroupDefinitions;
    public bool PatchSourceIdPassthrough => EffectiveFeatures.PatchSourceIdPassthrough;
    public bool NetworkDisabledDemo => EffectiveFeatures.NetworkDisabledDemo;
}
