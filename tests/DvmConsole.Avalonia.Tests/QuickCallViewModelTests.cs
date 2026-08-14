// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class QuickCallViewModelTests
    {
        [Fact]
        public void InvalidToneText_DoesNotBuildARequest()
        {
            var viewModel = new QuickCallViewModel
            {
                ToneA = "not-a-frequency",
                ToneB = "400.0",
            };

            Assert.False(viewModel.CanSend);
            Assert.False(viewModel.TryBuildRequest(out QuickCallRequest? request));
            Assert.Null(request);
            Assert.Contains("valid A and B", viewModel.ValidationMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void ValidToneText_BuildsTheWpfDurationOrderedQcIiStack()
        {
            var viewModel = new QuickCallViewModel
            {
                ToneA = "123.5",
                ToneB = "456.25",
            };

            Assert.True(viewModel.CanSend);
            Assert.True(viewModel.TryBuildRequest(out QuickCallRequest? request));
            Assert.NotNull(request);
            Assert.Equal(123.5, request!.ToneAHz);
            Assert.Equal(456.25, request.ToneBHz);
            Assert.True(request.SendStartSignal);
            Assert.True(request.ClearPageStateAfterSend);
            Assert.NotEmpty(request.Pcm);
            Assert.Equal(0, request.Pcm.Length % dvmconsole.TonePcmSequencer.FrameBytes);
        }

        [Fact]
        public void EmptyToneText_ReportsValidationWithoutGeneratingPcm()
        {
            var viewModel = new QuickCallViewModel();

            Assert.False(viewModel.CanSend);
            Assert.False(viewModel.TryBuildRequest(out QuickCallRequest? request));
            Assert.Null(request);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.ValidationMessage));
        }
    }
}
