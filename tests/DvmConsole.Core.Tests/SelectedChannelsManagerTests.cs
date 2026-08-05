// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/
/**
* GREEN contract gate for the portable generic dvmconsole.SelectedChannelsManager<T>
* implemented in DvmConsole.Core/Selection/. These tests lock the live-port
* contract transcribed from the WPF dvmconsole.SelectedChannelsManager
* (dvmconsole/SelectedChannelsManager.cs): membership, primary selection,
* constructor-injected visual/log effect delegates, exact delegate/event
* ordering, defensive selection snapshots, and null guards. Nothing here
* depends on WPF, Avalonia, native code, files or secrets; channel identity
* is reference-based, exactly like ChannelBox in the WPF source.
*/
#nullable enable
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Contract tests for the portable generic
    /// <see cref="SelectedChannelsManager{T}"/> selection-state manager.
    /// </summary>
    public class SelectedChannelsManagerTests
    {
        /// <summary>
        /// Minimal channel fake. Deliberately does NOT override Equals or
        /// GetHashCode, so membership is reference-identity based exactly
        /// like the WPF ChannelBox the manager currently tracks.
        /// </summary>
        private sealed class TestChannel
        {
            public TestChannel(string name)
            {
                Name = name;
            }

            public string Name { get; }
        }

        /// <summary>
        /// Creates a manager wired to a single ordered trace that records
        /// every visual effect and every event, so tests can lock the exact
        /// delegate/event ordering of each operation.
        /// </summary>
        private static (SelectedChannelsManager<TestChannel> Manager, List<string> Trace) CreateManager()
        {
            var trace = new List<string>();
            var manager = new SelectedChannelsManager<TestChannel>(
                selectionVisualChanged: (channel, selected) => trace.Add($"SelectionVisual({channel.Name}, {selected})"),
                primaryVisualChanged: (channel, selected) => trace.Add($"PrimaryVisual({channel.Name}, {selected})"),
                primaryChannelSet: channel => trace.Add($"PrimaryChannelSet({channel.Name})"));
            manager.SelectedChannelsChanged += () => trace.Add("SelectedChannelsChanged");
            manager.PrimaryChannelChanged += () => trace.Add("PrimaryChannelChanged");
            manager.ChannelSelectionChanged += (channel, selected) => trace.Add($"ChannelSelectionChanged({channel.Name}, {selected})");
            return (manager, trace);
        }

        /*
        ** Membership / idempotency
        */

        /// <summary>
        /// A fresh manager has an empty selection and no primary channel.
        /// </summary>
        [Fact]
        public void Ctor_Default_EmptySelectionAndNullPrimary()
        {
            var manager = new SelectedChannelsManager<TestChannel>();

            Assert.Null(manager.PrimaryChannel);
            Assert.Empty(manager.GetSelectedChannels());
        }

        /// <summary>
        /// Adding a new member makes it appear in the selection snapshot.
        /// </summary>
        [Fact]
        public void Add_NewMember_AppearsInSelection()
        {
            var (manager, _) = CreateManager();
            var channel = new TestChannel("A");

            manager.AddSelectedChannel(channel);

            Assert.Single(manager.GetSelectedChannels());
            Assert.Contains(channel, manager.GetSelectedChannels());
        }

        /// <summary>
        /// Adding an already-selected member is a full no-op: no visual
        /// effect, no event, and no membership change.
        /// </summary>
        [Fact]
        public void Add_Duplicate_IsFullNoOp()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);
            trace.Clear();

            manager.AddSelectedChannel(channel);

            Assert.Empty(trace);
            Assert.Single(manager.GetSelectedChannels());
        }

        /// <summary>
        /// Two distinct channel instances are both selectable at the same
        /// time (reference identity).
        /// </summary>
        [Fact]
        public void Add_TwoDistinctInstances_BothSelected()
        {
            var (manager, _) = CreateManager();
            var a = new TestChannel("A");
            var b = new TestChannel("B");

            manager.AddSelectedChannel(a);
            manager.AddSelectedChannel(b);

            Assert.Equal(2, manager.GetSelectedChannels().Count);
            Assert.Contains(a, manager.GetSelectedChannels());
            Assert.Contains(b, manager.GetSelectedChannels());
        }

        /// <summary>
        /// Two instances with the same Name are still distinct members:
        /// identity is reference-based, never name-based.
        /// </summary>
        [Fact]
        public void Add_TwoSameNameInstances_ReferenceIdentityKeepsBoth()
        {
            var (manager, _) = CreateManager();
            var first = new TestChannel("A");
            var second = new TestChannel("A");

            manager.AddSelectedChannel(first);
            manager.AddSelectedChannel(second);
            manager.RemoveSelectedChannel(first);

            Assert.Single(manager.GetSelectedChannels());
            Assert.Contains(second, manager.GetSelectedChannels());
            Assert.DoesNotContain(first, manager.GetSelectedChannels());
        }

        /// <summary>
        /// Removing a member removes it from the selection snapshot.
        /// </summary>
        [Fact]
        public void Remove_Member_RemovedFromSelection()
        {
            var (manager, _) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);

            manager.RemoveSelectedChannel(channel);

            Assert.Empty(manager.GetSelectedChannels());
        }

        /// <summary>
        /// Removing a non-member is a full no-op: no effect, no event, and
        /// the existing members stay untouched.
        /// </summary>
        [Fact]
        public void Remove_NonMember_IsNoOp()
        {
            var (manager, trace) = CreateManager();
            var member = new TestChannel("A");
            var outsider = new TestChannel("B");
            manager.AddSelectedChannel(member);
            trace.Clear();

            manager.RemoveSelectedChannel(outsider);

            Assert.Empty(trace);
            Assert.Single(manager.GetSelectedChannels());
            Assert.Contains(member, manager.GetSelectedChannels());
        }

        /// <summary>
        /// Removing an already-removed member a second time is a full no-op.
        /// </summary>
        [Fact]
        public void Remove_Twice_SecondRemovalIsNoOp()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);
            manager.RemoveSelectedChannel(channel);
            trace.Clear();

            manager.RemoveSelectedChannel(channel);

            Assert.Empty(trace);
            Assert.Empty(manager.GetSelectedChannels());
        }

        /// <summary>
        /// Clear removes every member from the selection.
        /// </summary>
        [Fact]
        public void Clear_RemovesAllMembers()
        {
            var (manager, _) = CreateManager();
            manager.AddSelectedChannel(new TestChannel("A"));
            manager.AddSelectedChannel(new TestChannel("B"));

            manager.ClearSelections();

            Assert.Empty(manager.GetSelectedChannels());
        }

        /// <summary>
        /// The manager stays usable after a clear: new members can be added.
        /// </summary>
        [Fact]
        public void Add_AfterClear_SelectionWorksAgain()
        {
            var (manager, _) = CreateManager();
            manager.AddSelectedChannel(new TestChannel("A"));
            manager.ClearSelections();
            var again = new TestChannel("B");

            manager.AddSelectedChannel(again);

            Assert.Single(manager.GetSelectedChannels());
            Assert.Contains(again, manager.GetSelectedChannels());
        }

        /*
        ** Primary channel
        */

        /// <summary>
        /// SetPrimary assigns the channel, fires the primary-channel-set
        /// hook first, then raises PrimaryChannelChanged.
        /// </summary>
        [Fact]
        public void SetPrimary_SetsAndRaisesPrimaryChannelChanged()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");

            manager.SetPrimaryChannel(channel);

            Assert.Same(channel, manager.PrimaryChannel);
            Assert.Equal(new[] { "PrimaryChannelSet(A)", "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// Re-setting the same channel as primary still raises the event and
        /// re-invokes the hook (unconditional raise).
        /// </summary>
        [Fact]
        public void SetPrimary_SameChannel_RaisesAgain()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.SetPrimaryChannel(channel);
            trace.Clear();

            manager.SetPrimaryChannel(channel);

            Assert.Same(channel, manager.PrimaryChannel);
            Assert.Equal(new[] { "PrimaryChannelSet(A)", "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// A channel can be primary without being selected: SetPrimary never
        /// adds to the selection and raises no selection events.
        /// </summary>
        [Fact]
        public void SetPrimary_ChannelNeedNotBeSelected()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");

            manager.SetPrimaryChannel(channel);

            Assert.Same(channel, manager.PrimaryChannel);
            Assert.Empty(manager.GetSelectedChannels());
            Assert.Equal(new[] { "PrimaryChannelSet(A)", "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// Switching the primary from one channel to another updates the
        /// property and raises PrimaryChannelChanged again.
        /// </summary>
        [Fact]
        public void SetPrimary_SwitchPrimary_RaisesAndUpdates()
        {
            var (manager, trace) = CreateManager();
            var a = new TestChannel("A");
            var b = new TestChannel("B");
            manager.SetPrimaryChannel(a);
            trace.Clear();

            manager.SetPrimaryChannel(b);

            Assert.Same(b, manager.PrimaryChannel);
            Assert.Equal(new[] { "PrimaryChannelSet(B)", "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// ClearPrimary nulls the property and raises PrimaryChannelChanged
        /// with no visual effects.
        /// </summary>
        [Fact]
        public void ClearPrimary_ClearsAndRaises()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.SetPrimaryChannel(channel);
            trace.Clear();

            manager.ClearPrimaryChannel();

            Assert.Null(manager.PrimaryChannel);
            Assert.Equal(new[] { "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// Clearing the primary when it is already null still raises
        /// PrimaryChannelChanged (unconditional raise).
        /// </summary>
        [Fact]
        public void ClearPrimary_WhenAlreadyNull_RaisesAgain()
        {
            var (manager, trace) = CreateManager();

            manager.ClearPrimaryChannel();

            Assert.Null(manager.PrimaryChannel);
            Assert.Equal(new[] { "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// Removing the primary member clears it FIRST (PrimaryChannelChanged
        /// fires before the visual effects and before the selection events),
        /// then emits the full remove effect/event sequence.
        /// </summary>
        [Fact]
        public void Remove_Primary_ClearsPrimaryBeforeSelectionEvents()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.SetPrimaryChannel(channel);
            manager.AddSelectedChannel(channel);
            trace.Clear();

            manager.RemoveSelectedChannel(channel);

            Assert.Null(manager.PrimaryChannel);
            Assert.Empty(manager.GetSelectedChannels());
            Assert.Equal(new[]
            {
                "PrimaryChannelChanged",
                "PrimaryVisual(A, False)",
                "SelectionVisual(A, False)",
                "ChannelSelectionChanged(A, False)",
                "SelectedChannelsChanged",
            }, trace);
        }

        /// <summary>
        /// ClearSelections leaves the primary channel untouched and raises no
        /// PrimaryChannelChanged (explicit compatibility quirk of the WPF
        /// source, locked as behavior).
        /// </summary>
        [Fact]
        public void Clear_LeavesPrimaryUntouched_NoPrimaryEvent()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.SetPrimaryChannel(channel);
            manager.AddSelectedChannel(channel);
            trace.Clear();

            manager.ClearSelections();

            Assert.Same(channel, manager.PrimaryChannel);
            Assert.Empty(manager.GetSelectedChannels());
            Assert.DoesNotContain("PrimaryChannelChanged", trace);
        }

        /*
        ** Effect delegates
        */

        /// <summary>
        /// Add fires the selection visual hook before ChannelSelectionChanged,
        /// which fires before SelectedChannelsChanged.
        /// </summary>
        [Fact]
        public void Add_SelectionVisualBeforeEvents()
        {
            var (manager, trace) = CreateManager();

            manager.AddSelectedChannel(new TestChannel("A"));

            Assert.Equal(new[]
            {
                "SelectionVisual(A, True)",
                "ChannelSelectionChanged(A, True)",
                "SelectedChannelsChanged",
            }, trace);
        }

        /// <summary>
        /// When the removed member is the primary, the primary visual hook
        /// fires before the selection visual hook.
        /// </summary>
        [Fact]
        public void Remove_Primary_PrimaryVisualBeforeSelectionVisual()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.SetPrimaryChannel(channel);
            manager.AddSelectedChannel(channel);
            trace.Clear();

            manager.RemoveSelectedChannel(channel);

            var visualTrace = trace
                .Where(t => t.StartsWith("PrimaryVisual", StringComparison.Ordinal) || t.StartsWith("SelectionVisual", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(new[] { "PrimaryVisual(A, False)", "SelectionVisual(A, False)" }, visualTrace);
        }

        /// <summary>
        /// Every removed member gets the unconditional primary visual
        /// false, even when it was never the primary; a non-primary remove
        /// leaves the primary channel untouched.
        /// </summary>
        [Fact]
        public void Remove_NonPrimary_StillInvokesPrimaryVisualFalse()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            var primary = new TestChannel("P");
            manager.SetPrimaryChannel(primary);
            manager.AddSelectedChannel(channel);
            trace.Clear();

            manager.RemoveSelectedChannel(channel);

            Assert.Same(primary, manager.PrimaryChannel);
            Assert.Equal(new[]
            {
                "PrimaryVisual(A, False)",
                "SelectionVisual(A, False)",
                "ChannelSelectionChanged(A, False)",
                "SelectedChannelsChanged",
            }, trace);
        }

        /// <summary>
        /// SetPrimary passes the exact channel instance to the
        /// primaryChannelSet hook (the WPF Log.WriteLine replacement) before
        /// raising PrimaryChannelChanged.
        /// </summary>
        [Fact]
        public void SetPrimary_HookReceivesExactChannelInstance()
        {
            var hookChannels = new List<TestChannel>();
            var trace = new List<string>();
            var manager = new SelectedChannelsManager<TestChannel>(
                selectionVisualChanged: (channel, selected) => trace.Add($"SelectionVisual({channel.Name}, {selected})"),
                primaryVisualChanged: (channel, selected) => trace.Add($"PrimaryVisual({channel.Name}, {selected})"),
                primaryChannelSet: channel =>
                {
                    hookChannels.Add(channel);
                    trace.Add($"PrimaryChannelSet({channel.Name})");
                });
            manager.PrimaryChannelChanged += () => trace.Add("PrimaryChannelChanged");
            var channel = new TestChannel("A");

            manager.SetPrimaryChannel(channel);

            Assert.Same(channel, Assert.Single(hookChannels));
            Assert.Equal(new[] { "PrimaryChannelSet(A)", "PrimaryChannelChanged" }, trace);
        }

        /// <summary>
        /// The parameterless construction (all effect delegates null) is
        /// valid: every operation succeeds and effects are silent no-ops.
        /// </summary>
        [Fact]
        public void Ctor_NullDelegates_OperationsSucceed()
        {
            var manager = new SelectedChannelsManager<TestChannel>();
            var channel = new TestChannel("A");
            var other = new TestChannel("B");

            manager.AddSelectedChannel(channel);
            manager.AddSelectedChannel(other);
            manager.SetPrimaryChannel(channel);
            manager.ClearSelections();
            manager.ClearPrimaryChannel();

            Assert.Null(manager.PrimaryChannel);
            Assert.Empty(manager.GetSelectedChannels());
        }

        /// <summary>
        /// Clear fires the selection visual false and ChannelSelectionChanged
        /// per member in set enumeration order, then a single aggregate
        /// SelectedChannelsChanged.
        /// </summary>
        [Fact]
        public void Clear_InvokesSelectionVisualFalsePerChannelInOrder()
        {
            var (manager, trace) = CreateManager();
            manager.AddSelectedChannel(new TestChannel("A"));
            manager.AddSelectedChannel(new TestChannel("B"));
            manager.AddSelectedChannel(new TestChannel("C"));
            trace.Clear();

            manager.ClearSelections();

            Assert.Equal(new[]
            {
                "SelectionVisual(A, False)",
                "ChannelSelectionChanged(A, False)",
                "SelectionVisual(B, False)",
                "ChannelSelectionChanged(B, False)",
                "SelectionVisual(C, False)",
                "ChannelSelectionChanged(C, False)",
                "SelectedChannelsChanged",
            }, trace);
        }

        /*
        ** Defensive snapshot
        */

        /// <summary>
        /// GetSelectedChannels returns a copy at call time, not a live view:
        /// a snapshot taken before a removal still contains the member.
        /// </summary>
        [Fact]
        public void GetSelectedChannels_ReturnsCopyNotLiveView()
        {
            var (manager, _) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);
            var snapshot = manager.GetSelectedChannels();

            manager.RemoveSelectedChannel(channel);

            Assert.Contains(channel, snapshot);
            Assert.Empty(manager.GetSelectedChannels());
        }

        /// <summary>
        /// Mutating the returned collection cannot affect the manager: the
        /// snapshot is a detached copy.
        /// </summary>
        [Fact]
        public void GetSelectedChannels_MutatingReturnedCollection_DoesNotAffectManager()
        {
            var (manager, _) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);

            var snapshot = manager.GetSelectedChannels();
            ((ICollection<TestChannel>)snapshot).Clear();

            Assert.Single(manager.GetSelectedChannels());
            Assert.Contains(channel, manager.GetSelectedChannels());
        }

        /// <summary>
        /// Each call returns a distinct collection instance, both reflecting
        /// the current state.
        /// </summary>
        [Fact]
        public void GetSelectedChannels_PerCallReturnsDistinctInstances()
        {
            var (manager, _) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);

            var first = manager.GetSelectedChannels();
            var second = manager.GetSelectedChannels();

            Assert.NotSame(first, second);
            Assert.Single(first);
            Assert.Single(second);
            Assert.Contains(channel, first);
            Assert.Contains(channel, second);
        }

        /*
        ** Null guards
        */

        /// <summary>
        /// Adding null throws ArgumentNullException and changes no state and
        /// raises no events.
        /// </summary>
        [Fact]
        public void Add_Null_ThrowsAndLeavesStateUnchanged()
        {
            var (manager, trace) = CreateManager();

            Assert.Throws<ArgumentNullException>(() => manager.AddSelectedChannel(null!));

            Assert.Empty(manager.GetSelectedChannels());
            Assert.Null(manager.PrimaryChannel);
            Assert.Empty(trace);
        }

        /// <summary>
        /// Removing null throws ArgumentNullException and leaves the existing
        /// members and events untouched.
        /// </summary>
        [Fact]
        public void Remove_Null_ThrowsAndLeavesStateUnchanged()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.AddSelectedChannel(channel);
            trace.Clear();

            Assert.Throws<ArgumentNullException>(() => manager.RemoveSelectedChannel(null!));

            Assert.Single(manager.GetSelectedChannels());
            Assert.Contains(channel, manager.GetSelectedChannels());
            Assert.Empty(trace);
        }

        /// <summary>
        /// Setting null as primary throws ArgumentNullException and leaves
        /// the current primary and events untouched.
        /// </summary>
        [Fact]
        public void SetPrimary_Null_ThrowsAndLeavesStateUnchanged()
        {
            var (manager, trace) = CreateManager();
            var channel = new TestChannel("A");
            manager.SetPrimaryChannel(channel);
            trace.Clear();

            Assert.Throws<ArgumentNullException>(() => manager.SetPrimaryChannel(null!));

            Assert.Same(channel, manager.PrimaryChannel);
            Assert.Empty(trace);
        }
    } // public class SelectedChannelsManagerTests
} // namespace DvmConsole.Core.Tests
