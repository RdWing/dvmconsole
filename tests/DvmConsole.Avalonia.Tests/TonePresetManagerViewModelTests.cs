// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless Gate 5.3 generated tone preset manager.
    /// The VM owns managed rows and request payloads only; shell persistence,
    /// audio playback, and transmission remain outside this slice.
    /// </summary>
    public sealed class TonePresetManagerViewModelTests
    {
        [Fact]
        public void Load_PreservesStableIdsAndSteps_AndFallsBackFromStaleTarget()
        {
            var vm = new TonePresetManagerViewModel(
                new[]
                {
                    new UserSettingsTonePresetConfig
                    {
                        Id = "stable-tone",
                        DisplayName = "Dispatch",
                        TargetResourceKey = "missing|99",
                        Steps = new List<UserSettingsTonePresetStep>
                        {
                            new() { Kind = "tone", FrequencyHz = 1200, DurationSeconds = 1.5 },
                            new() { Kind = "hold", FrequencyHz = 0, DurationSeconds = 0.75 },
                        },
                    },
                },
                new[] { new TonePresetTarget("system|42", "System 42") });

            var preset = Assert.Single(vm.Presets);
            Assert.Equal("stable-tone", preset.Id);
            Assert.Equal("Dispatch", preset.DisplayName);
            Assert.Equal(2, preset.Steps.Count);
            Assert.Equal("system|42", vm.SelectedTarget!.Key);
            Assert.Equal("system|42", vm.SelectedPreset!.TargetResourceKey);
        }

        [Fact]
        public void AddDeleteAndMove_KeepStableIdsAndOrderedSteps()
        {
            var vm = new TonePresetManagerViewModel(
                Array.Empty<UserSettingsTonePresetConfig>(),
                new[] { new TonePresetTarget("system|42", "System 42") });

            vm.AddPreset();
            var preset = Assert.Single(vm.Presets);
            string presetId = preset.Id;
            vm.AddHold();
            vm.AddTone();

            Assert.Equal(3, preset.Steps.Count);
            vm.MoveStep(preset.Steps[2], -1);
            Assert.Equal("tone", preset.Steps[1].Kind, ignoreCase: true);
            vm.DeleteStep(preset.Steps[0]);
            Assert.Equal(2, preset.Steps.Count);
            Assert.Equal(presetId, preset.Id);

            vm.DeleteSelected();
            Assert.Empty(vm.Presets);
            Assert.Null(vm.SelectedPreset);
        }

        [Fact]
        public void Commit_RaisesDetachedSnapshot_WithClampsAndTarget()
        {
            var vm = new TonePresetManagerViewModel(
                new[]
                {
                    new UserSettingsTonePresetConfig
                    {
                        Id = "tone-1",
                        DisplayName = "  Tone  ",
                        TargetResourceKey = "system|42",
                        Steps = new List<UserSettingsTonePresetStep>
                        {
                            new() { Kind = "tone", FrequencyHz = 0, DurationSeconds = 0.01 },
                        },
                    },
                },
                new[] { new TonePresetTarget("system|42", "System 42") });
            IReadOnlyList<UserSettingsTonePresetConfig>? saved = null;
            vm.SaveRequested += snapshot => saved = snapshot;

            vm.Commit();

            var preset = Assert.Single(saved!);
            var step = Assert.Single(preset.Steps);
            Assert.Equal("Tone", preset.DisplayName);
            Assert.Equal("system|42", preset.TargetResourceKey);
            Assert.Equal(1d, step.FrequencyHz);
            Assert.Equal(0.25d, step.DurationSeconds);
            Assert.NotSame(vm.Presets[0].Steps[0], step);
        }

        [Fact]
        public void EmptyPreset_DoesNotRaisePreviewOrSend()
        {
            var vm = new TonePresetManagerViewModel(
                new[]
                {
                    new UserSettingsTonePresetConfig
                    {
                        Id = "empty",
                        DisplayName = "Empty",
                        TargetResourceKey = "system|42",
                        Steps = new List<UserSettingsTonePresetStep>(),
                    },
                },
                new[] { new TonePresetTarget("system|42", "System 42") });
            int previewCount = 0;
            int sendCount = 0;
            vm.PreviewRequested += _ => previewCount++;
            vm.SendRequested += _ => sendCount++;

            vm.Preview();
            vm.Send();

            Assert.Equal(0, previewCount);
            Assert.Equal(0, sendCount);
        }

        [Fact]
        public void PreviewAndSend_RaiseNormalizedPcmRequests_ForSelectedTarget()
        {
            var vm = new TonePresetManagerViewModel(
                new[]
                {
                    new UserSettingsTonePresetConfig
                    {
                        Id = "tone-1",
                        DisplayName = "Tone",
                        TargetResourceKey = "system|42",
                        Steps = new List<UserSettingsTonePresetStep>
                        {
                            new() { Kind = "tone", FrequencyHz = 0, DurationSeconds = 0.01 },
                        },
                    },
                },
                new[] { new TonePresetTarget("system|42", "System 42") });
            TonePresetRequest? preview = null;
            TonePresetRequest? send = null;
            vm.PreviewRequested += request => preview = request;
            vm.SendRequested += request => send = request;

            vm.Preview();
            vm.Send();

            Assert.Equal("tone-1", preview!.PresetId);
            Assert.Equal("system|42", preview.TargetResourceKey);
            Assert.True(preview.Pcm.Length > 0);
            Assert.Equal(preview.Pcm, send!.Pcm);
            Assert.Equal(preview.TargetResourceKey, send.TargetResourceKey);
        }

        [Fact]
        public void ViewModelSource_DoesNotOwnFilesAudioOrTransmission()
        {
            string path = Path.Combine(
                RepositoryRoot(),
                "DvmConsole.Avalonia",
                "ViewModels",
                "TonePresetManagerViewModel.cs");
            string source = File.ReadAllText(path);

            Assert.DoesNotContain("using System.IO", source, StringComparison.Ordinal);
            Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IAudio", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TransmitPcmAsync", source, StringComparison.Ordinal);
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
