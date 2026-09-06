// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class BackgroundAppearanceControllerTests
{
    [Fact]
    public async Task DisposalCancelsAnInFlightImportWithoutPublishingSettings()
    {
        var store = new BlockingAssetStore();
        var settings = new UserSettings();
        int persistenceCount = 0;
        var controller = new BackgroundAppearanceController(
            store,
            settings,
            ImmediateTestUiDispatcher.Instance,
            () => persistenceCount++);
        await using var content = new MemoryStream([1, 2, 3]);

        Task<bool> importing = controller.SetAsync(
            "background.png",
            "image/png",
            content);
        await store.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task disposing = controller.DisposeAsync().AsTask();
        await store.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(disposing.IsCompleted);

        store.ReleaseCleanup.TrySetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await importing.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(settings.UserBackgroundAssetId);
        Assert.Null(settings.UserBackgroundImage);
        Assert.Equal(0, persistenceCount);
    }

    private sealed class BlockingAssetStore : IAssetStore
    {
        public TaskCompletionSource ImportEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<AssetDescriptor> ImportAsync(
            string displayName,
            string mediaType,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            ImportEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await ReleaseCleanup.Task;
                throw;
            }
        }

        public ValueTask<Stream> OpenReadAsync(
            AssetId id,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The cancelled import must not be opened.");

        public async IAsyncEnumerable<AssetDescriptor> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

}
