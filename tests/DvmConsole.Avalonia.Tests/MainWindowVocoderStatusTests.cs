// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Native;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the optional startup vocoder status exposed by
    /// MainWindowViewModel. Existing constructor paths remain uncomposed.
    /// </summary>
    public sealed class MainWindowVocoderStatusTests
    {
        [Fact]
        public void ExistingConstructorPaths_LeaveVocoderStatusNull()
        {
            Assert.Null(new MainWindowViewModel().VocoderStatus);
            Assert.Null(new MainWindowViewModel(null).VocoderStatus);
            Assert.Null(new MainWindowViewModel(null, null).VocoderStatus);
            Assert.Null(new MainWindowViewModel(null, null, null).VocoderStatus);
            Assert.Null(new MainWindowViewModel(null, null, null, null).VocoderStatus);
        }

        [Fact]
        public void VocoderStatusProperty_IsReadOnlyNullableString()
        {
            var property = typeof(MainWindowViewModel)
                .GetProperty(nameof(MainWindowViewModel.VocoderStatus))!;

            Assert.Equal(typeof(string), property.PropertyType);
            Assert.False(property.CanWrite);
        }

        [Fact]
        public void ReadyResult_ProducesStableReadyStatus()
        {
            var vm = CreateViewModel(new VocoderReadinessResult(true, "libvocoder", null));

            Assert.Equal("libvocoder ready", vm.VocoderStatus);
        }

        [Fact]
        public void FailureResult_ExposesDiagnosticVerbatim()
        {
            const string diagnostic =
                "The libvocoder native library could not be loaded. It is required for operation.";
            var vm = CreateViewModel(new VocoderReadinessResult(false, "libvocoder", diagnostic));

            Assert.Equal(diagnostic, vm.VocoderStatus);
        }

        private static MainWindowViewModel CreateViewModel(VocoderReadinessResult result)
            => new(null, null, null, null, result);
    }
}
