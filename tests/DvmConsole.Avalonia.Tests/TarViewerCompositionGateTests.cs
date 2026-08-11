// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Parent RED for TAR runtime composition: App builds one recorder from
    /// normalized persisted settings, MainWindow exposes an owner-bound viewer
    /// entry point and visible dependency status, and the existing full
    /// constructor grows only by trailing optional viewer dependencies.
    /// </summary>
    public sealed class TarViewerCompositionGateTests
    {
        [Fact]
        public void AppCreatesTarRecorderFromNormalizedPersistedSettings()
        {
            string root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-composition-" + Guid.NewGuid().ToString("N"));
            string settingsPath = Path.Combine(root, "settings.json");
            string fallbackRoot = Path.Combine(root, "fallback");
            string configuredRoot = Path.Combine(root, "configured");
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(settingsPath));
                persistence.Save(
                    "  " + configuredRoot + "  ",
                    new Dictionary<string, TarChannelConfig>
                    {
                        ["SYS|77"] = new TarChannelConfig
                        {
                            Enabled = true,
                            RetentionDays = 14,
                            IgnoredSubscriberIds = new List<uint> { 2, 9 },
                        },
                    });

                TarRecorder recorder = App.CreateTarRecorder(persistence, fallbackRoot);

                Assert.Equal(configuredRoot, recorder.ResolveRecordingRoot());
                var metadata = new TarRecordingMetadata
                {
                    Direction = TarRecordingDirection.RX,
                    RecordingSourceType = TarRecordingSourceType.InboundRadio,
                    Protocol = "DMR",
                    UtcStartTime = DateTime.UtcNow,
                    SystemName = "SYS",
                    ChannelName = "Dispatch",
                    TalkgroupId = 77,
                    SubscriberId = 456,
                    StreamId = 1,
                };

                Assert.True(recorder.TryStartRecording(
                    metadata,
                    "sys|77",
                    "Dispatch",
                    "77",
                    out string sessionKey));
                Assert.NotEmpty(sessionKey);
                recorder.StopAllSessions(DateTime.UtcNow);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AppAndMainWindowExposeTarViewerEntryPoints()
        {
            NativeMenuItem item = App.CreateTarViewerMenuItem(null);
            Assert.Equal("TAR Viewer", item.Header);

            MethodInfo? openMethod = typeof(MainWindow).GetMethod(
                "OpenTarViewer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(openMethod);
            Assert.Empty(openMethod!.GetParameters());
            Assert.Equal(typeof(void), openMethod.ReturnType);
        }

        [Fact]
        public void MainWindowViewModelExposesVisibleTarViewerStatus()
        {
            var viewModel = new MainWindowViewModel();
            Assert.Empty(viewModel.TarViewerStatusMessage);

            viewModel.TarViewerStatusMessage = "TAR Viewer unavailable.";
            Assert.Equal("TAR Viewer unavailable.", viewModel.TarViewerStatusMessage);
        }

        [Fact]
        public void MainWindowFullConstructorAppendsViewerDependenciesAfterPtt()
        {
            ConstructorInfo constructor = Assert.Single(
                typeof(MainWindow)
                    .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .Where(candidate => candidate.GetParameters().Any(
                        parameter => parameter.ParameterType == typeof(AudioSettingsPersistence))
                        && candidate.GetParameters().Any(
                            parameter => parameter.ParameterType == typeof(AliasResolver))));
            ParameterInfo[] parameters = constructor.GetParameters();

            Assert.Equal(typeof(PttSettingsPersistence), parameters[^4].ParameterType);
            Assert.Equal(typeof(TarRecorder), parameters[^3].ParameterType);
            Assert.Equal(typeof(IAudioWaveFilePlayer), parameters[^2].ParameterType);
            Assert.Equal(typeof(TarViewerColumnSettingsPersistence), parameters[^1].ParameterType);
            Assert.True(parameters[^4].IsOptional);
            Assert.Null(parameters[^4].DefaultValue);
            Assert.True(parameters[^3].IsOptional);
            Assert.Null(parameters[^3].DefaultValue);
            Assert.True(parameters[^2].IsOptional);
            Assert.Null(parameters[^2].DefaultValue);
            Assert.True(parameters[^1].IsOptional);
            Assert.Null(parameters[^1].DefaultValue);
        }
    }
}
