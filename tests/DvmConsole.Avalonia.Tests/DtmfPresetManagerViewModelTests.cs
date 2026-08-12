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
    /// RED contract for the Gate 5.4 headless DTMF preset manager.
    /// The VM owns managed rows and detached DTMF PCM requests only; shell
    /// persistence, playback, and transmission remain outside this slice.
    /// </summary>
    public sealed class DtmfPresetManagerViewModelTests
    {
        [Fact]
        public void Load_PreservesStableIdsAndOrderedSteps_AndFallsBackFromStaleTarget()
        {
            var vm = new DtmfPresetManagerViewModel(
                new[]
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "stable-dtmf",
                        DisplayName = "Dispatch Digits",
                        TargetResourceKey = "missing|99",
                        Steps = new List<UserSettingsDtmfPresetStep>
                        {
                            new() { Kind = "digit", Digit = "5", DurationSeconds = 0.5 },
                            new() { Kind = "hold", Digit = "ignored", DurationSeconds = 0.75 },
                            new() { Kind = "digit", Digit = "a", DurationSeconds = 0.25 },
                        },
                    },
                },
                new[] { new DtmfPresetTarget("system|42", "System 42") });

            var preset = Assert.Single(vm.Presets);
            Assert.Equal("stable-dtmf", preset.Id);
            Assert.Equal("Dispatch Digits", preset.DisplayName);
            Assert.Equal(3, preset.Steps.Count);
            Assert.Equal("5", preset.Steps[0].Digit);
            Assert.Equal("hold", preset.Steps[1].Kind, ignoreCase: true);
            Assert.Equal("A", preset.Steps[2].Digit);
            Assert.Equal("system|42", vm.SelectedTarget!.Key);
            Assert.Equal("system|42", vm.SelectedPreset!.TargetResourceKey);
        }

        [Fact]
        public void AddDeleteAndMove_KeepStableIdsAndOrderedSteps()
        {
            var vm = new DtmfPresetManagerViewModel(
                Array.Empty<UserSettingsDtmfPresetConfig>(),
                new[] { new DtmfPresetTarget("system|42", "System 42") });

            vm.AddPreset();
            var preset = Assert.Single(vm.Presets);
            string presetId = preset.Id;
            vm.AddHold();
            vm.AddDigit();

            Assert.Equal(3, preset.Steps.Count);
            vm.MoveStep(preset.Steps[2], -1);
            Assert.Equal("digit", preset.Steps[1].Kind, ignoreCase: true);
            vm.DeleteStep(preset.Steps[0]);
            Assert.Equal(2, preset.Steps.Count);
            Assert.Equal(presetId, preset.Id);

            vm.DeleteSelected();
            Assert.Empty(vm.Presets);
            Assert.Null(vm.SelectedPreset);
        }

        [Fact]
        public void Commit_RaisesDetachedSnapshot_WithAlphabetAndDurationNormalization()
        {
            var vm = new DtmfPresetManagerViewModel(
                new[]
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "dtmf-1",
                        DisplayName = "  DTMF  ",
                        TargetResourceKey = "system|42",
                        Steps = new List<UserSettingsDtmfPresetStep>
                        {
                            new() { Kind = "digit", Digit = "  ab ", DurationSeconds = 0.01 },
                            new() { Kind = "hold", Digit = "9", DurationSeconds = 11 },
                            new() { Kind = "digit", Digit = "z", DurationSeconds = 0.5 },
                        },
                    },
                },
                new[] { new DtmfPresetTarget("system|42", "System 42") });
            IReadOnlyList<UserSettingsDtmfPresetConfig>? saved = null;
            vm.SaveRequested += snapshot => saved = snapshot;

            vm.Commit();

            var preset = Assert.Single(saved!);
            Assert.Equal("DTMF", preset.DisplayName);
            Assert.Equal("system|42", preset.TargetResourceKey);
            Assert.Equal(new[] { "A", string.Empty, "1" }, preset.Steps.Select(step => step.Digit));
            Assert.Equal(new[] { 0.25d, 10d, 0.5d }, preset.Steps.Select(step => step.DurationSeconds));
            Assert.Equal("hold", preset.Steps[1].Kind, ignoreCase: true);
            Assert.NotSame(vm.Presets[0].Steps[0], preset.Steps[0]);
        }

        [Fact]
        public void EmptyPreset_DoesNotRaisePreviewOrSend()
        {
            var vm = new DtmfPresetManagerViewModel(
                new[]
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "empty",
                        DisplayName = "Empty",
                        TargetResourceKey = "system|42",
                        Steps = new List<UserSettingsDtmfPresetStep>(),
                    },
                },
                new[] { new DtmfPresetTarget("system|42", "System 42") });
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
        public void PreviewAndSend_RaiseNormalizedDtmfPcmRequests_ForSelectedTarget()
        {
            var vm = new DtmfPresetManagerViewModel(
                new[]
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "dtmf-1",
                        DisplayName = "DTMF",
                        TargetResourceKey = "system|42",
                        Steps = new List<UserSettingsDtmfPresetStep>
                        {
                            new() { Kind = "digit", Digit = "5", DurationSeconds = 0.25 },
                        },
                    },
                },
                new[] { new DtmfPresetTarget("system|42", "System 42") });
            DtmfPresetRequest? preview = null;
            DtmfPresetRequest? send = null;
            vm.PreviewRequested += request => preview = request;
            vm.SendRequested += request => send = request;

            vm.Preview();
            vm.Send();

            Assert.Equal("dtmf-1", preview!.PresetId);
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
                "DtmfPresetManagerViewModel.cs");
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
