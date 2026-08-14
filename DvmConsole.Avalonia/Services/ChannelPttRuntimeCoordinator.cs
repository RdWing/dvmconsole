// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Owns one channel-card's momentary PTT lifecycle. A press resolves the
    /// pressed slot once, starts one router capture for that target, and a
    /// release ends that capture. The active state remains visible until the
    /// asynchronous router teardown completes, so a pointer-release race
    /// cannot permit a second capture to start over a live one.
    /// </summary>
    public sealed class ChannelPttRuntimeCoordinator : IAsyncDisposable
    {
        private readonly TransmitTargetResolver targetResolver;
        private readonly Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit;
        private readonly Func<Task> endTransmit;
        private readonly Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers;
        private readonly Func<bool> canStartTransmit;
        private readonly Func<TransmitTarget, bool> isTargetAvailable;
        private readonly Action<string>? reportStatus;
        private readonly SemaphoreSlim requestGate = new(1, 1);
        private readonly object stateGate = new();
        private ChannelSlotViewModel? activeSlot;
        private TransmitTarget? activeTarget;
        private bool disposed;

        public ChannelPttRuntimeCoordinator(
            TransmitTargetResolver targetResolver,
            Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit,
            Func<Task> endTransmit,
            Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers,
            Func<bool> canStartTransmit,
            Func<TransmitTarget, bool> isTargetAvailable,
            Action<string>? reportStatus = null)
        {
            this.targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
            this.beginTransmit = beginTransmit ?? throw new ArgumentNullException(nameof(beginTransmit));
            this.endTransmit = endTransmit ?? throw new ArgumentNullException(nameof(endTransmit));
            this.clearReceiveBuffers = clearReceiveBuffers ?? throw new ArgumentNullException(nameof(clearReceiveBuffers));
            this.canStartTransmit = canStartTransmit ?? throw new ArgumentNullException(nameof(canStartTransmit));
            this.isTargetAvailable = isTargetAvailable ?? throw new ArgumentNullException(nameof(isTargetAvailable));
            this.reportStatus = reportStatus;
        }

        /// <summary>True from before router start until router teardown completes.</summary>
        public bool IsTransmitActive
        {
            get
            {
                lock (stateGate)
                {
                    return activeTarget is not null;
                }
            }
        }

        /// <summary>The single target owned by the active card press, or null.</summary>
        public TransmitTarget? ActiveTarget
        {
            get
            {
                lock (stateGate)
                {
                    return activeTarget;
                }
            }
        }

        /// <summary>
        /// Handles a momentary card press. Duplicate presses while the card
        /// capture is active are ignored; other cards are rejected until the
        /// current release has completed.
        /// </summary>
        public async Task HandlePointerDownAsync(ChannelSlotViewModel slot)
        {
            ArgumentNullException.ThrowIfNull(slot);
            await requestGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (stateGate)
                {
                    if (disposed || activeTarget is not null)
                    {
                        return;
                    }
                }

                if (!CanStart())
                {
                    Report("Channel PTT blocked: another transmit is active.");
                    return;
                }

                var target = ResolveTarget(slot);
                if (target is not { } resolved)
                {
                    Report("Channel PTT unavailable: channel has no valid transmit target.");
                    return;
                }

                if (!IsTargetAvailable(resolved))
                {
                    Report("Channel PTT unavailable: FNE target is not connected.");
                    return;
                }

                lock (stateGate)
                {
                    if (disposed || activeTarget is not null)
                    {
                        return;
                    }

                    activeSlot = slot;
                    activeTarget = resolved;
                    slot.PttEngaged = true;
                }

                try
                {
                    var targets = new[] { resolved };
                    clearReceiveBuffers(targets);
                    await beginTransmit(targets).ConfigureAwait(false);
                }
                catch
                {
                    ClearActiveState();
                    throw;
                }
            }
            finally
            {
                requestGate.Release();
            }
        }

        /// <summary>
        /// Releases the current card capture. The active state and card visual
        /// remain asserted until the router's asynchronous end has completed.
        /// Repeated releases are no-ops.
        /// </summary>
        public async Task HandlePointerUpAsync()
        {
            await requestGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ChannelSlotViewModel? slot;
                lock (stateGate)
                {
                    if (disposed && activeTarget is null)
                    {
                        return;
                    }

                    if (activeTarget is null)
                    {
                        return;
                    }

                    slot = activeSlot;
                }

                try
                {
                    await endTransmit().ConfigureAwait(false);
                }
                finally
                {
                    ClearActiveState(slot);
                }
            }
            finally
            {
                requestGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await requestGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ChannelSlotViewModel? slot;
                bool shouldEnd;
                lock (stateGate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    disposed = true;
                    slot = activeSlot;
                    shouldEnd = activeTarget is not null;
                }

                try
                {
                    if (shouldEnd)
                    {
                        await endTransmit().ConfigureAwait(false);
                    }
                }
                finally
                {
                    ClearActiveState(slot);
                }
            }
            finally
            {
                requestGate.Release();
                requestGate.Dispose();
            }
        }

        private TransmitTarget? ResolveTarget(ChannelSlotViewModel slot)
        {
            try
            {
                return targetResolver.Resolve(slot.ChannelName);
            }
            catch (Exception exception)
            {
                Report($"Channel PTT target resolution failed: {exception.Message}");
                return null;
            }
        }

        private bool CanStart()
        {
            try
            {
                return canStartTransmit();
            }
            catch (Exception exception)
            {
                Report($"Channel PTT availability check failed: {exception.Message}");
                return false;
            }
        }

        private bool IsTargetAvailable(TransmitTarget target)
        {
            try
            {
                return isTargetAvailable(target);
            }
            catch (Exception exception)
            {
                Report($"Channel PTT target availability check failed: {exception.Message}");
                return false;
            }
        }

        private void ClearActiveState(ChannelSlotViewModel? expectedSlot = null)
        {
            lock (stateGate)
            {
                var slot = expectedSlot ?? activeSlot;
                if (slot is not null)
                {
                    slot.PttEngaged = false;
                }

                activeSlot = null;
                activeTarget = null;
            }
        }

        private void Report(string message)
        {
            try
            {
                reportStatus?.Invoke(message);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Channel PTT status callback failed: {exception.Message}");
            }
        }
    }
}
