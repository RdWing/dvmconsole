// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Networking;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class SubscriberCommandViewModelTests
    {
        [Fact]
        public async Task InvalidDestinationIsDisabledBeforeExecutorRuns()
        {
            int executions = 0;
            var viewModel = CreateViewModel(
                SubscriberCommandKind.Page,
                (_, _) =>
                {
                    executions++;
                    return Task.FromResult(Succeeded());
                });

            viewModel.DestinationId = "0";

            Assert.False(viewModel.CanSubmit);
            var result = await viewModel.SubmitAsync();

            Assert.Equal(SubscriberCommandStatus.InvalidRequest, result.Status);
            Assert.Equal(0, executions);
        }

        [Fact]
        public async Task SubmitProjectsSelectedSystemAsP25Command()
        {
            SubscriberCommandRequest? request = null;
            var viewModel = CreateViewModel(
                SubscriberCommandKind.RadioCheck,
                (candidate, _) =>
                {
                    request = candidate;
                    return Task.FromResult(Succeeded());
                });
            viewModel.DestinationId = "123456";

            var result = await viewModel.SubmitAsync();

            Assert.Equal(SubscriberCommandStatus.Succeeded, result.Status);
            Assert.NotNull(request);
            Assert.Equal("subscriber-window", request!.OwnerId);
            Assert.Equal("System A", request.SystemName);
            Assert.Equal("100001", request.SourceId);
            Assert.Equal("123456", request.DestinationId);
            Assert.Equal(SubscriberCommandKind.RadioCheck, request.Kind);
            Assert.Equal(SubscriberCommandMode.P25, request.Mode);
            Assert.Equal("sent", viewModel.StatusMessage);
        }

        [Fact]
        public async Task InFlightSubmissionDisablesDuplicateSubmission()
        {
            var completion = new TaskCompletionSource<SubscriberCommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            int executions = 0;
            var viewModel = CreateViewModel(
                SubscriberCommandKind.Inhibit,
                (_, _) =>
                {
                    executions++;
                    return completion.Task;
                });
            viewModel.DestinationId = "123456";

            Task<SubscriberCommandResult> first = viewModel.SubmitAsync();
            Assert.True(viewModel.IsSubmitting);
            Assert.False(viewModel.CanSubmit);

            var duplicate = await viewModel.SubmitAsync();

            Assert.Equal(SubscriberCommandStatus.Busy, duplicate.Status);
            Assert.Equal(1, executions);

            completion.SetResult(Succeeded());
            var firstResult = await first;

            Assert.Equal(SubscriberCommandStatus.Succeeded, firstResult.Status);
            Assert.False(viewModel.IsSubmitting);
            Assert.True(viewModel.CanSubmit);
        }

        [Fact]
        public void BlankSystemsAreRemovedAndFirstUsableSystemIsSelected()
        {
            var viewModel = new SubscriberCommandViewModel(
                new Codeplug.System?[]
                {
                    null,
                    new Codeplug.System { Name = "  ", Rid = "100002" },
                    MakeSystem("System A", "100001"),
                },
                SubscriberCommandKind.Uninhibit,
                "subscriber-window",
                (_, _) => Task.FromResult(Succeeded()));

            Assert.Single(viewModel.Systems);
            Assert.Equal("System A", viewModel.SelectedSystem!.Name);
            Assert.Equal(SubscriberCommandKind.Uninhibit, viewModel.CommandKind);
            Assert.Equal(SubscriberCommandMode.P25, viewModel.Mode);
        }

        private static SubscriberCommandViewModel CreateViewModel(
            SubscriberCommandKind kind,
            Func<SubscriberCommandRequest, CancellationToken, Task<SubscriberCommandResult>> execute)
            => new SubscriberCommandViewModel(
                new Codeplug.System?[] { MakeSystem("System A", "100001") },
                kind,
                "subscriber-window",
                execute);

        private static Codeplug.System MakeSystem(string name, string rid)
            => new Codeplug.System
            {
                Name = name,
                Rid = rid,
            };

        private static SubscriberCommandResult Succeeded()
            => new SubscriberCommandResult(SubscriberCommandStatus.Succeeded, "sent");
    }
}
