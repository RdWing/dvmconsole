// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Owns the shell-side runtime lifecycle for patch and multi-select PTT.
    /// Each request carries a copied group-member snapshot from
    /// <see cref="ViewModels.PatchGroupsViewModel"/>. The coordinator resolves
    /// those members at request time, reconciles the active group union, and
    /// serializes one shared router capture across group changes.
    ///
    /// This state is intentionally separate from
    /// <see cref="PatchForwardingCoordinator"/>: receive patch forwarding and
    /// operator PTT have different stream lifecycles and must not share
    /// per-forward state.
    /// </summary>
    public sealed class PatchPttRuntimeCoordinator : IAsyncDisposable
    {
        private readonly TransmitTargetResolver targetResolver;
        private readonly Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit;
        private readonly Func<Task> endTransmit;
        private readonly Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers;
        private readonly Func<bool> canStartTransmit;
        private readonly Func<TransmitTarget, bool> isTargetAvailable;
        private readonly Action<string>? reportStatus;
        private readonly Func<TransmitTarget, bool> isForwardTargetActive;
        private readonly SemaphoreSlim requestGate = new(1, 1);
        private readonly object stateGate = new();
        private readonly Dictionary<string, IReadOnlyList<TransmitTarget>> activeGroups =
            new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<TransmitTarget> activeTargets = Array.Empty<TransmitTarget>();
        private bool transmitActive;
        private bool disposed;

        public PatchPttRuntimeCoordinator(
            TransmitTargetResolver targetResolver,
            Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit,
            Func<Task> endTransmit,
            Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers,
            Func<bool> canStartTransmit)
            : this(targetResolver, beginTransmit, endTransmit, clearReceiveBuffers,
                canStartTransmit, _ => true, null, _ => false)
        {
        }

        public PatchPttRuntimeCoordinator(
            TransmitTargetResolver targetResolver,
            Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit,
            Func<Task> endTransmit,
            Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers,
            Func<bool> canStartTransmit,
            Func<TransmitTarget, bool> isTargetAvailable)
            : this(targetResolver, beginTransmit, endTransmit, clearReceiveBuffers,
                canStartTransmit, isTargetAvailable, null, _ => false)
        {
        }

        public PatchPttRuntimeCoordinator(
            TransmitTargetResolver targetResolver,
            Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit,
            Func<Task> endTransmit,
            Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers,
            Func<bool> canStartTransmit,
            Func<TransmitTarget, bool> isTargetAvailable,
            Action<string>? reportStatus)
            : this(targetResolver, beginTransmit, endTransmit, clearReceiveBuffers,
                canStartTransmit, isTargetAvailable, reportStatus, _ => false)
        {
        }

        public PatchPttRuntimeCoordinator(
            TransmitTargetResolver targetResolver,
            Func<IReadOnlyList<TransmitTarget>, Task> beginTransmit,
            Func<Task> endTransmit,
            Action<IReadOnlyList<TransmitTarget>> clearReceiveBuffers,
            Func<bool> canStartTransmit,
            Func<TransmitTarget, bool> isTargetAvailable,
            Action<string>? reportStatus,
            Func<TransmitTarget, bool> isForwardTargetActive)
        {
            this.targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
            this.beginTransmit = beginTransmit ?? throw new ArgumentNullException(nameof(beginTransmit));
            this.endTransmit = endTransmit ?? throw new ArgumentNullException(nameof(endTransmit));
            this.clearReceiveBuffers = clearReceiveBuffers ?? throw new ArgumentNullException(nameof(clearReceiveBuffers));
            this.canStartTransmit = canStartTransmit ?? throw new ArgumentNullException(nameof(canStartTransmit));
            this.isTargetAvailable = isTargetAvailable ?? throw new ArgumentNullException(nameof(isTargetAvailable));
            this.reportStatus = reportStatus;
            this.isForwardTargetActive = isForwardTargetActive
                ?? throw new ArgumentNullException(nameof(isForwardTargetActive));
        }

        /// <summary>True from before router start until router teardown completes.</summary>
        public bool IsTransmitActive
        {
            get
            {
                lock (stateGate)
                    return transmitActive;
            }
        }

        /// <summary>A stable copy of the targets in the current shared capture.</summary>
        public IReadOnlyList<TransmitTarget> ActiveTargets
        {
            get
            {
                lock (stateGate)
                    return activeTargets.ToArray();
            }
        }

        /// <summary>
        /// Applies one group's active/inactive edge. Requests are serialized so
        /// a rapid group toggle cannot overlap router begin/end calls.
        /// </summary>
        public async Task HandleRequestAsync(
            string? groupName,
            bool isActive,
            IReadOnlyList<PatchTalkgroupMember>? members)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                return;

            await requestGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (stateGate)
                {
                    if (disposed)
                        return;
                }

                var name = groupName.Trim();
                if (isActive && !canStartTransmit())
                {
                    reportStatus?.Invoke("Patch PTT blocked: dashboard PTT is active.");
                    return;
                }

                lock (stateGate)
                {
                    if (isActive)
                    {
                        var targets = ResolveMembers(members);
                        if (targets.Count == 0)
                        {
                            activeGroups.Remove(name);
                            reportStatus?.Invoke("Patch PTT: no valid transmit targets.");
                        }
                        else
                        {
                            activeGroups[name] = targets;
                        }
                    }
                    else
                    {
                        activeGroups.Remove(name);
                    }
                }

                var nextTargets = BuildUnionTargets();
                IReadOnlyList<TransmitTarget> currentTargets;
                bool wasTransmitActive;
                lock (stateGate)
                {
                    currentTargets = activeTargets;
                    wasTransmitActive = transmitActive;
                }

                if (TargetsEqual(currentTargets, nextTargets))
                    return;

                if (wasTransmitActive)
                {
                    // Keep transmitActive true while awaiting the router:
                    // RX speaker suppression must cover the entire teardown.
                    await endTransmit().ConfigureAwait(false);
                    lock (stateGate)
                    {
                        transmitActive = false;
                        activeTargets = Array.Empty<TransmitTarget>();
                    }
                }

                if (nextTargets.Count == 0)
                    return;

                lock (stateGate)
                {
                    transmitActive = true;
                    activeTargets = nextTargets;
                }

                try
                {
                    clearReceiveBuffers(nextTargets);
                    await beginTransmit(nextTargets).ConfigureAwait(false);
                }
                catch
                {
                    lock (stateGate)
                    {
                        activeGroups.Clear();
                        transmitActive = false;
                        activeTargets = Array.Empty<TransmitTarget>();
                    }
                    throw;
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
                bool wasTransmitActive;
                lock (stateGate)
                {
                    if (disposed)
                        return;

                    disposed = true;
                    activeGroups.Clear();
                    wasTransmitActive = transmitActive;
                }

                if (wasTransmitActive)
                    await endTransmit().ConfigureAwait(false);

                lock (stateGate)
                {
                    transmitActive = false;
                    activeTargets = Array.Empty<TransmitTarget>();
                }
            }
            finally
            {
                requestGate.Release();
            }
        }

        private IReadOnlyList<TransmitTarget> ResolveMembers(
            IReadOnlyList<PatchTalkgroupMember>? members)
        {
            if (members is null)
                return Array.Empty<TransmitTarget>();

            var targets = new List<TransmitTarget>();
            foreach (var member in members)
            {
                if (member is null)
                    continue;

                var target = targetResolver.ResolveTalkgroup(member.SystemName, member.Tgid);
                if (target is { } resolved
                    && IsTargetAvailable(resolved)
                    && !IsForwardTargetActive(resolved)
                    && !ContainsTarget(targets, resolved))
                {
                    targets.Add(resolved);
                }
            }

            return targets;
        }

        private IReadOnlyList<TransmitTarget> BuildUnionTargets()
        {
            IReadOnlyList<IReadOnlyList<TransmitTarget>> groups;
            lock (stateGate)
                groups = activeGroups.Values.ToArray();

            var targets = new List<TransmitTarget>();
            foreach (var group in groups)
            {
                foreach (var target in group)
                {
                    if (!ContainsTarget(targets, target))
                        targets.Add(target);
                }
            }

            return targets;
        }

        private bool IsTargetAvailable(TransmitTarget target)
        {
            try
            {
                return isTargetAvailable(target);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Patch PTT target availability check failed: {exception.Message}");
                return false;
            }
        }

        private bool IsForwardTargetActive(TransmitTarget target)
        {
            try
            {
                return isForwardTargetActive(target);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Patch PTT forward-target check failed: {exception.Message}");
                return true;
            }
        }

        private static bool ContainsTarget(
            IReadOnlyList<TransmitTarget> targets,
            TransmitTarget candidate)
            => targets.Any(target =>
                string.Equals(target.SystemName, candidate.SystemName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.TalkgroupId, candidate.TalkgroupId, StringComparison.Ordinal)
                && target.Slot == candidate.Slot
                && target.Mode == candidate.Mode
                && target.SourceId == candidate.SourceId);

        private static bool TargetsEqual(
            IReadOnlyList<TransmitTarget> left,
            IReadOnlyList<TransmitTarget> right)
        {
            if (left.Count != right.Count)
                return false;

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i].SystemName, right[i].SystemName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(left[i].TalkgroupId, right[i].TalkgroupId, StringComparison.Ordinal)
                    || left[i].Slot != right[i].Slot
                    || left[i].Mode != right[i].Mode
                    || left[i].SourceId != right[i].SourceId)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
