#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Avalonia;
using DvmConsole.Avalonia.Services;
using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class CodeplugReloadCoordinatorTests
    {
        private const string MinimalYaml = @"
 systems:
   - name: Reloaded System
     address: 127.0.0.1
     port: 62031
     password: placeholder
     peerId: 1
     rid: '1'
 zones:
   - name: Reloaded Zone
     channels:
       - name: Reloaded Channel
         system: Reloaded System
         tgid: '99'
         slot: 1
         mode: dmr
";

        [Fact]
        public async Task FailedLoad_LeavesCurrentRuntimeUntouched()
        {
            var stopped = 0;
            var replaced = 0;
            var statuses = new List<string>();
            var coordinator = new CodeplugReloadCoordinator(
                _ => CodeplugLoader.LoadFromText("not: [valid"),
                () =>
                {
                    stopped++;
                    return Task.CompletedTask;
                },
                _ =>
                {
                    replaced++;
                    return Task.CompletedTask;
                },
                statuses.Add);

            var succeeded = await coordinator.ReloadAsync("broken.yml", CancellationToken.None);

            Assert.False(succeeded);
            Assert.Equal(0, stopped);
            Assert.Equal(0, replaced);
            Assert.Contains("failed", Assert.Single(statuses), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SuccessfulLoad_StopsBeforeReplacingExactlyOnce()
        {
            var order = new List<string>();
            Codeplug? applied = null;
            var coordinator = new CodeplugReloadCoordinator(
                _ => CodeplugLoader.LoadFromText(MinimalYaml),
                () =>
                {
                    order.Add("stop");
                    return Task.CompletedTask;
                },
                codeplug =>
                {
                    order.Add("replace");
                    applied = codeplug;
                    return Task.CompletedTask;
                });

            var succeeded = await coordinator.ReloadAsync("valid.yml", CancellationToken.None);

            Assert.True(succeeded);
            Assert.Equal(new[] { "stop", "replace" }, order);
            Assert.NotNull(applied);
            Assert.Equal("Reloaded System", applied!.Systems[0].Name);
        }

        [Fact]
        public async Task SuccessfulLoad_PreparesBeforeStoppingAndDiscardsPreparedStateOnFailure()
        {
            var order = new List<string>();
            var discarded = 0;
            var coordinator = new CodeplugReloadCoordinator(
                _ => CodeplugLoader.LoadFromText(MinimalYaml),
                () =>
                {
                    order.Add("stop");
                    throw new InvalidOperationException("stop failed");
                },
                _ =>
                {
                    order.Add("apply");
                    return Task.CompletedTask;
                },
                prepareCodeplug: _ =>
                {
                    order.Add("prepare");
                    return Task.CompletedTask;
                },
                discardPreparedCodeplug: () =>
                {
                    discarded++;
                    return Task.CompletedTask;
                });

            var succeeded = await coordinator.ReloadAsync("valid.yml", CancellationToken.None);

            Assert.False(succeeded);
            Assert.Equal(new[] { "prepare", "stop" }, order);
            Assert.Equal(1, discarded);
        }

        [Fact]
        public async Task CancellationBeforeStop_LeavesCurrentRuntimeUntouched()
        {
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var stopped = 0;
            var coordinator = new CodeplugReloadCoordinator(
                _ => CodeplugLoader.LoadFromText(MinimalYaml),
                () =>
                {
                    stopped++;
                    return Task.CompletedTask;
                },
                _ => Task.CompletedTask);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                coordinator.ReloadAsync("valid.yml", cancellation.Token));

            Assert.Equal(0, stopped);
        }

        [Fact]
        public async Task ApplyFailure_ReportsFailureAfterStopping()
        {
            var statuses = new List<string>();
            var coordinator = new CodeplugReloadCoordinator(
                _ => CodeplugLoader.LoadFromText(MinimalYaml),
                () => Task.CompletedTask,
                _ => throw new InvalidOperationException("apply failed"),
                statuses.Add);

            var succeeded = await coordinator.ReloadAsync("valid.yml", CancellationToken.None);

            Assert.False(succeeded);
            Assert.Contains("apply failed", Assert.Single(statuses));
        }

        [Fact]
        public void MainWindowSource_DefersCandidateAndActivatesAfterAwaitedTeardown()
        {
            string source = File.ReadAllText(MainWindowSourcePath());
            string app = File.ReadAllText(AppSourcePath());

            Assert.Contains("deferRuntimeActivation", source);
            Assert.Contains("internal void ActivateRuntime()", source);
            Assert.Contains("StartRestoredAsync", source);
            Assert.Contains("if (runtimeActivated)", source);

            int disposeIndex = app.IndexOf("await current.DisposeRuntimeAsync()", StringComparison.Ordinal);
            int activateIndex = app.IndexOf("candidate.ActivateRuntime()", disposeIndex, StringComparison.Ordinal);
            int showIndex = app.IndexOf("candidate.Show()", activateIndex, StringComparison.Ordinal);

            Assert.True(disposeIndex >= 0);
            Assert.True(activateIndex > disposeIndex);
            Assert.True(showIndex > activateIndex);
        }

        [Fact]
        public void AudioDeviceChange_RefreshesSettingsAndRestartsCapture()
        {
            string source = File.ReadAllText(MainWindowSourcePath());

            Assert.Contains("private void OnAudioDevicesChanged", source);
            Assert.Contains("macCatalog.DevicesChanged += OnAudioDevicesChanged;", source);
            Assert.Contains("macCatalog.DevicesChanged -= OnAudioDevicesChanged;", source);
            Assert.Contains("viewModel.AudioSettings?.Refresh();", source);
            Assert.Contains("talkgroupAudioRouter?.RequestCaptureRestartAsync()", source);
        }

        [Fact]
        public async Task SuccessfulReload_ResumesApplyOnCapturedUiContextAfterAsyncStop()
        {
            var previousContext = SynchronizationContext.Current;
            using var uiContext = new PumpSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(uiContext);
            var stopGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SynchronizationContext? applyContext = null;
            var coordinator = new CodeplugReloadCoordinator(
                _ => CodeplugLoader.LoadFromText(MinimalYaml),
                () => stopGate.Task,
                _ =>
                {
                    applyContext = SynchronizationContext.Current;
                    return Task.CompletedTask;
                });

            try
            {
                Task<bool> reload = coordinator.ReloadAsync("valid.yml", CancellationToken.None);
                Assert.False(reload.IsCompleted);

                stopGate.SetResult(true);
                uiContext.RunUntil(reload);

                Assert.True(await reload);
                Assert.Same(uiContext, applyContext);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        [Fact]
        public void ComposedNativeServices_AreOwnedAndDisposedWithWindowRuntime()
        {
            string source = File.ReadAllText(MainWindowSourcePath());
            string app = File.ReadAllText(AppSourcePath());

            Assert.Contains("bool ownsRuntimeServices", source);
            Assert.Contains("ownsRuntimeServices: true", app);
            Assert.Contains("if (ownsRuntimeServices && hotkeys is not null)", source);
            Assert.Contains("await macAudioDeviceCatalog.DisposeAsync()", source);
        }

        private static string MainWindowSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "MainWindow.axaml.cs");

        private static string AppSourcePath()
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "App.axaml.cs");

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
            private readonly AutoResetEvent callbackAvailable = new(false);

            public override void Post(SendOrPostCallback d, object? state)
            {
                callbacks.Enqueue((d, state));
                callbackAvailable.Set();
            }

            public void RunUntil(Task task)
            {
                Stopwatch timeout = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (callbacks.TryDequeue(out var callback))
                    {
                        SynchronizationContext.SetSynchronizationContext(this);
                        callback.Callback(callback.State);
                    }
                    else if (!callbackAvailable.WaitOne(TimeSpan.FromMilliseconds(10))
                        && timeout.Elapsed > TimeSpan.FromSeconds(2))
                    {
                        throw new TimeoutException("Timed out pumping the UI synchronization context.");
                    }
                }

                task.GetAwaiter().GetResult();
            }

            public void Dispose() => callbackAvailable.Dispose();
        }
    }
}
