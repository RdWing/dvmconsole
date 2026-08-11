// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using dvmconsole;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Avalonia.Views;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Parent RED contract for the TAR viewer shell. Playback remains a WAV-specific
    /// platform seam; reveal and confirmation are injected so the window has no
    /// Process, MessageBox, or audio-format ownership.
    /// </summary>
    public sealed class TarViewerWindowTests
    {
        [Fact]
        public void WindowHasSinglePublicInjectedConstructorAndInternalClass()
        {
            Type type = typeof(TarViewerWindow);
            Assert.False(type.IsPublic);
            var constructors = type.GetConstructors(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            var constructor = Assert.Single(constructors);
            var parameters = constructor.GetParameters();
            Assert.Equal(
                new[]
                {
                    typeof(TarViewerViewModel),
                    typeof(IAudioWaveFilePlayer),
                    typeof(IFileRevealService),
                    typeof(IConfirmationService),
                },
                Array.ConvertAll(parameters, parameter => parameter.ParameterType));
        }

        [Fact]
        public void WindowSourcePinsViewerShellActionsAndCleanup()
        {
            string source = File.ReadAllText(SourcePath());
            Assert.Contains("DataContext = viewModel", source);
            Assert.Contains("PlayWavAsync", source);
            Assert.Contains("StopAsync", source);
            Assert.Contains("RevealAsync", source);
            Assert.Contains("ConfirmAsync", source);
            Assert.Contains("Recorder.DeleteRecording", source);
            Assert.Contains("CancellationTokenSource", source);
            Assert.Contains("OnClosed", source);
        }

        [Fact]
        public void XamlContainsViewerFiltersRowsAndActions()
        {
            string source = File.ReadAllText(XamlPath());
            Assert.Contains("x:ClassModifier=\"internal\"", source);
            Assert.Contains("TarViewerViewModel", source);
            Assert.Contains("Rows", source);
            Assert.Contains("SearchText", source);
            Assert.Contains("SelectedDirectionFilter", source);
            Assert.Contains("SelectedProtocolFilter", source);
            Assert.Contains("SelectedEncryptionFilter", source);
            Assert.Contains("Play_Click", source);
            Assert.Contains("Stop_Click", source);
            Assert.Contains("OpenFolder_Click", source);
            Assert.Contains("Delete_Click", source);
            Assert.Contains("ClearFilters_Click", source);
        }

        private static string SourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Views", "TarViewerWindow.axaml.cs");

        private static string XamlPath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Views", "TarViewerWindow.axaml");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
