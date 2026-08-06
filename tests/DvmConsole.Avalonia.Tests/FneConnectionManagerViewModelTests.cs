// SPDX-License-Identifier: AGPL-3.0-only
/**
* Contract gate for the Avalonia FNE connection-manager view-model slice:
*
*   DvmConsole.Avalonia.ViewModels.FneSystemConnectionViewModel
*   DvmConsole.Avalonia.ViewModels.FneConnectionManagerViewModel
*   MainWindowViewModel.FneConnections (get-only, constructed empty)
*
* FneSystemConnectionViewModel wraps one dvmconsole.Codeplug.System into a
* sealed observable row: read-only identity/config projection (SystemName,
* Identity, Address, Port, Encrypted, PeerId, Endpoint = Address + ":" +
* Port preserving address text), observable bool flags IsConnected / IsBusy
* / IsStarted, derived StatusText ("Connected"/"Disconnected"),
* ToggleButtonText ("Stop"/"Start"), and ButtonsEnabled (!IsBusy).
* Notification contract: setting IsConnected raises exactly IsConnected,
* StatusText, ToggleButtonText; setting IsBusy raises exactly IsBusy,
* ButtonsEnabled; setting IsStarted raises exactly IsStarted; same-value
* assignments raise nothing. Secrets (Password / PresharedKey / Rid) never
* appear on the public row surface.
*
* FneConnectionManagerViewModel is a sealed observable manager with a
* parameterless ctor and a ctor taking IReadOnlyList<Codeplug.System>?
* (null means empty). Rows skip null/blank names, collapse
* case-insensitive duplicates with the last config winning, and sort by
* SystemName using StringComparer.OrdinalIgnoreCase. Aggregate surface:
* Systems, HasSystems, HasNoSystems, AnyConnected, ConnectedSystemSummary
* (first connected sorted row formatted "SystemName Endpoint", or null)
* with change-only notifications. ApplyState(string, bool, bool, bool)
* matches rows case-insensitively, is a no-op for unknown/blank names,
* assigns state verbatim (clearing busy), and preserves row notification
* behavior. StartSystem / StopSystem / RestartSystem are no-ops for
* unknown/blank or busy rows; otherwise they set the row IsBusy=true
* BEFORE raising the corresponding Action<string> event exactly once with
* the canonical row.SystemName. There is no automatic connection, network,
* or protocol behavior anywhere in this slice.
*
* The tests are fully headless and pure managed: no Avalonia.Headless
* package, window, display, native call, file, or secret is involved.
*
* This file is the executable contract for the managed frontend slice.
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract gate for <c>FneSystemConnectionViewModel</c>,
    /// <c>FneConnectionManagerViewModel</c>, and the
    /// <c>MainWindowViewModel.FneConnections</c> property.
    /// </summary>
    public sealed class FneConnectionManagerViewModelTests
    {
        // ---- Fixtures ---------------------------------------------------------

        /// <summary>
        /// Builds a codeplug system with a secret-bearing config; the tests
        /// prove those secrets never leak onto the view-model surface.
        /// </summary>
        private static Codeplug.System MakeSystem(
            string? name,
            string address = "127.0.0.1",
            int port = 54000,
            string identity = "TEST-CALLSIGN",
            uint peerId = 1u,
            bool encrypted = true)
        {
            return new Codeplug.System
            {
                Name = name,
                Identity = identity,
                Address = address,
                Port = port,
                Encrypted = encrypted,
                PeerId = peerId,
                Password = "top-secret-password",
                PresharedKey = "top-secret-psk",
                Rid = "3133700"
            };
        }

        // ---- A. Row shape and no secrets ---------------------------------------

        /// <summary>
        /// Shape gate for the row: sealed observable type, read-only
        /// identity/config properties with exact types, writable observable
        /// bool flags, read-only derived strings, an exact single-argument
        /// ctor, and NO secret properties (Password / PresharedKey / Rid)
        /// anywhere on the public surface.
        /// </summary>
        [Fact]
        public void RowShape_SealedObservable_IdentityReadOnly_NoSecrets_ExactCtor()
        {
            var row = typeof(FneSystemConnectionViewModel);

            Assert.True(row.IsSealed);
            Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(row));

            Assert.Equal(typeof(string), row.GetProperty(nameof(FneSystemConnectionViewModel.SystemName))!.PropertyType);
            Assert.Equal(typeof(string), row.GetProperty(nameof(FneSystemConnectionViewModel.Identity))!.PropertyType);
            Assert.Equal(typeof(string), row.GetProperty(nameof(FneSystemConnectionViewModel.Address))!.PropertyType);
            Assert.Equal(typeof(int), row.GetProperty(nameof(FneSystemConnectionViewModel.Port))!.PropertyType);
            Assert.Equal(typeof(bool), row.GetProperty(nameof(FneSystemConnectionViewModel.Encrypted))!.PropertyType);
            Assert.Equal(typeof(uint), row.GetProperty(nameof(FneSystemConnectionViewModel.PeerId))!.PropertyType);
            Assert.Equal(typeof(string), row.GetProperty(nameof(FneSystemConnectionViewModel.Endpoint))!.PropertyType);
            Assert.Equal(typeof(string), row.GetProperty(nameof(FneSystemConnectionViewModel.StatusText))!.PropertyType);
            Assert.Equal(typeof(string), row.GetProperty(nameof(FneSystemConnectionViewModel.ToggleButtonText))!.PropertyType);
            Assert.Equal(typeof(bool), row.GetProperty(nameof(FneSystemConnectionViewModel.ButtonsEnabled))!.PropertyType);
            Assert.Equal(typeof(bool), row.GetProperty(nameof(FneSystemConnectionViewModel.IsConnected))!.PropertyType);
            Assert.Equal(typeof(bool), row.GetProperty(nameof(FneSystemConnectionViewModel.IsBusy))!.PropertyType);
            Assert.Equal(typeof(bool), row.GetProperty(nameof(FneSystemConnectionViewModel.IsStarted))!.PropertyType);

            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.SystemName))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.Identity))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.Address))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.Port))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.Encrypted))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.PeerId))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.Endpoint))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.StatusText))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.ToggleButtonText))!.CanWrite);
            Assert.False(row.GetProperty(nameof(FneSystemConnectionViewModel.ButtonsEnabled))!.CanWrite);
            Assert.True(row.GetProperty(nameof(FneSystemConnectionViewModel.IsConnected))!.CanWrite);
            Assert.True(row.GetProperty(nameof(FneSystemConnectionViewModel.IsBusy))!.CanWrite);
            Assert.True(row.GetProperty(nameof(FneSystemConnectionViewModel.IsStarted))!.CanWrite);

            Assert.Null(row.GetProperty("Password"));
            Assert.Null(row.GetProperty("PresharedKey"));
            Assert.Null(row.GetProperty("Rid"));

            var ctor = row.GetConstructors();
            Assert.Single(ctor);
            var parameters = ctor[0].GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(Codeplug.System), parameters[0].ParameterType);
        }

        /// <summary>
        /// The row projects the configured identity and connection facts
        /// verbatim, and never surfaces the configured secrets.
        /// </summary>
        [Fact]
        public void Row_Ctor_ExposesConfigIdentityAndEndpoint()
        {
            var system = MakeSystem("Alpha", address: "192.168.1.10", port: 54000, identity: "KD8RHO", peerId: 123456u);
            var row = new FneSystemConnectionViewModel(system);

            Assert.Equal("Alpha", row.SystemName);
            Assert.Equal("KD8RHO", row.Identity);
            Assert.Equal("192.168.1.10", row.Address);
            Assert.Equal(54000, row.Port);
            Assert.True(row.Encrypted);
            Assert.Equal(123456u, row.PeerId);
            Assert.Equal("192.168.1.10:54000", row.Endpoint);
        }

        /// <summary>
        /// Endpoint is a plain concatenation of the address text and the
        /// port; the address is preserved verbatim, including surrounding
        /// whitespace, with no trimming or normalization.
        /// </summary>
        [Fact]
        public void Row_Endpoint_PreservesAddressTextVerbatim()
        {
            var row = new FneSystemConnectionViewModel(
                MakeSystem("Alpha", address: " 10.0.0.5 ", port: 12345));

            Assert.Equal(" 10.0.0.5 :12345", row.Endpoint);
        }

        /// <summary>
        /// A fresh row is disconnected, unbusy, and unstarted, with the
        /// derived Start/Disconnected/Enabled values.
        /// </summary>
        [Fact]
        public void Row_InitialState_FlagsAndDerivedValues()
        {
            var row = new FneSystemConnectionViewModel(MakeSystem("Alpha"));

            Assert.False(row.IsConnected);
            Assert.False(row.IsBusy);
            Assert.False(row.IsStarted);
            Assert.Equal("Disconnected", row.StatusText);
            Assert.Equal("Start", row.ToggleButtonText);
            Assert.True(row.ButtonsEnabled);
        }

        /// <summary>
        /// Changing IsConnected raises exactly IsConnected, StatusText,
        /// ToggleButtonText in that order, and flips the derived values;
        /// the reverse transition raises the same names again.
        /// </summary>
        [Fact]
        public void Row_SetIsConnected_RaisesExactlyThreeInOrder_UpdatesDerived()
        {
            var row = new FneSystemConnectionViewModel(MakeSystem("Alpha"));
            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            row.IsConnected = true;

            Assert.Equal(
                new List<string?> { "IsConnected", "StatusText", "ToggleButtonText" },
                raised);
            Assert.Equal("Connected", row.StatusText);
            Assert.Equal("Stop", row.ToggleButtonText);

            raised.Clear();
            row.IsConnected = false;

            Assert.Equal(
                new List<string?> { "IsConnected", "StatusText", "ToggleButtonText" },
                raised);
            Assert.Equal("Disconnected", row.StatusText);
            Assert.Equal("Start", row.ToggleButtonText);
        }

        /// <summary>
        /// Changing IsBusy raises exactly IsBusy, ButtonsEnabled in that
        /// order, and ButtonsEnabled mirrors !IsBusy.
        /// </summary>
        [Fact]
        public void Row_SetIsBusy_RaisesExactlyTwoInOrder_UpdatesButtonsEnabled()
        {
            var row = new FneSystemConnectionViewModel(MakeSystem("Alpha"));
            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            row.IsBusy = true;

            Assert.Equal(
                new List<string?> { "IsBusy", "ButtonsEnabled" },
                raised);
            Assert.False(row.ButtonsEnabled);

            raised.Clear();
            row.IsBusy = false;

            Assert.Equal(
                new List<string?> { "IsBusy", "ButtonsEnabled" },
                raised);
            Assert.True(row.ButtonsEnabled);
        }

        /// <summary>
        /// Changing IsStarted raises exactly IsStarted, and nothing else.
        /// </summary>
        [Fact]
        public void Row_SetIsStarted_RaisesExactlyOne()
        {
            var row = new FneSystemConnectionViewModel(MakeSystem("Alpha"));
            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            row.IsStarted = true;

            Assert.Equal(new List<string?> { "IsStarted" }, raised);
            Assert.True(row.IsStarted);
        }

        /// <summary>
        /// Assigning a value identical to the current one raises no
        /// notification at all, for every observable flag.
        /// </summary>
        [Fact]
        public void Row_SameValueAssignments_RaiseNothing()
        {
            var row = new FneSystemConnectionViewModel(MakeSystem("Alpha"));
            row.IsConnected = true;
            row.IsBusy = true;
            row.IsStarted = true;
            var raised = new List<string?>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            row.IsConnected = true;
            row.IsBusy = true;
            row.IsStarted = true;

            Assert.Empty(raised);
        }

        // ---- B. Manager: construction and row building --------------------------

        /// <summary>
        /// The parameterless ctor produces an empty manager: no rows,
        /// HasNoSystems true, nothing connected, no summary.
        /// </summary>
        [Fact]
        public void Manager_ParameterlessCtor_EmptyManager()
        {
            var manager = new FneConnectionManagerViewModel();

            Assert.Empty(manager.Systems);
            Assert.False(manager.HasSystems);
            Assert.True(manager.HasNoSystems);
            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);
        }

        /// <summary>
        /// A null system list is treated as an empty list.
        /// </summary>
        [Fact]
        public void Manager_NullListCtor_EmptyManager()
        {
            var manager = new FneConnectionManagerViewModel(null);

            Assert.Empty(manager.Systems);
            Assert.False(manager.HasSystems);
            Assert.True(manager.HasNoSystems);
            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);
        }

        /// <summary>
        /// Rows with null, empty, or whitespace-only names are skipped; a
        /// list of only such entries yields an empty manager.
        /// </summary>
        [Fact]
        public void Manager_AllBlankOrNullRows_EmptyManager()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem(null),
                    MakeSystem(""),
                    MakeSystem("   "),
                    null
                });

            Assert.Empty(manager.Systems);
            Assert.False(manager.HasSystems);
            Assert.True(manager.HasNoSystems);
        }

        /// <summary>
        /// Rows are sorted by SystemName with OrdinalIgnoreCase, blank and
        /// null names/entries are skipped, and case-insensitive duplicate
        /// names collapse with the LAST config winning (verbatim name and
        /// connection facts).
        /// </summary>
        [Fact]
        public void Manager_BuildsRows_Sorted_DedupCaseInsensitiveLastWins_SkipsBlankNull()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Bravo", address: "10.0.0.2"),
                    null,
                    MakeSystem("alpha", address: "10.0.0.1"),
                    MakeSystem("", address: "10.0.0.9"),
                    MakeSystem("ALPHA", address: "10.0.0.3"), // duplicate: last wins
                    MakeSystem(null),
                    MakeSystem("  ", address: "10.0.0.8"),
                    MakeSystem("Charlie", address: "10.0.0.4")
                });

            Assert.Equal(3, manager.Systems.Count);
            Assert.Equal("ALPHA", manager.Systems[0].SystemName);
            Assert.Equal("10.0.0.3", manager.Systems[0].Address);
            Assert.Equal("Bravo", manager.Systems[1].SystemName);
            Assert.Equal("Charlie", manager.Systems[2].SystemName);
            Assert.True(manager.HasSystems);
            Assert.False(manager.HasNoSystems);
            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);
        }

        /// <summary>
        /// Every row of a freshly built manager is disconnected, unbusy,
        /// and unstarted.
        /// </summary>
        [Fact]
        public void Manager_InitialRows_DisconnectedUnbusyUnstarted()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha"),
                    MakeSystem("Beta")
                });

            foreach (var row in manager.Systems)
            {
                Assert.False(row.IsConnected);
                Assert.False(row.IsBusy);
                Assert.False(row.IsStarted);
                Assert.True(row.ButtonsEnabled);
            }
        }

        // ---- B. Manager: ApplyState ----------------------------------------------

        /// <summary>
        /// Applying state to an unknown system name is a no-op: no row
        /// changes, no notifications on the manager, and nothing raised by
        /// any row.
        /// </summary>
        [Fact]
        public void Manager_ApplyState_UnknownName_NoOp_NoNotifications()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha") });
            var row = manager.Systems[0];
            var managerRaised = new List<string?>();
            var rowRaised = new List<string?>();
            manager.PropertyChanged += (_, e) => managerRaised.Add(e.PropertyName);
            row.PropertyChanged += (_, e) => rowRaised.Add(e.PropertyName);

            manager.ApplyState("Nope", isConnected: true, isBusy: true, isStarted: true);

            Assert.False(row.IsConnected);
            Assert.False(row.IsBusy);
            Assert.False(row.IsStarted);
            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);
            Assert.Empty(managerRaised);
            Assert.Empty(rowRaised);
        }

        /// <summary>
        /// Applying state with a blank (empty, whitespace, or null) system
        /// name is a no-op.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Manager_ApplyState_BlankName_NoOp(string? systemName)
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha") });
            var row = manager.Systems[0];
            var managerRaised = new List<string?>();
            manager.PropertyChanged += (_, e) => managerRaised.Add(e.PropertyName);

            manager.ApplyState(systemName!, isConnected: true, isBusy: true, isStarted: true);

            Assert.False(row.IsConnected);
            Assert.False(row.IsBusy);
            Assert.False(row.IsStarted);
            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);
            Assert.Empty(managerRaised);
        }

        /// <summary>
        /// ApplyState matches rows case-insensitively, assigns state
        /// verbatim with the row's own change-only notifications, and
        /// raises the change-only aggregates AnyConnected and
        /// ConnectedSystemSummary on the manager.
        /// </summary>
        [Fact]
        public void Manager_ApplyState_CaseInsensitive_UpdatesMatchingRow_NotifiesRowAndAggregates()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha", address: "10.0.0.1"),
                    MakeSystem("Beta", address: "10.0.0.2")
                });
            var alpha = manager.Systems[0];
            var beta = manager.Systems[1];
            var managerRaised = new List<string?>();
            var rowRaised = new List<string?>();
            manager.PropertyChanged += (_, e) => managerRaised.Add(e.PropertyName);
            alpha.PropertyChanged += (_, e) => rowRaised.Add(e.PropertyName);

            manager.ApplyState("aLpHa", isConnected: true, isBusy: true, isStarted: true);

            Assert.True(alpha.IsConnected);
            Assert.True(alpha.IsBusy);
            Assert.True(alpha.IsStarted);
            Assert.False(beta.IsConnected);
            Assert.False(beta.IsBusy);
            Assert.False(beta.IsStarted);

            rowRaised.Sort(StringComparer.Ordinal);
            Assert.Equal(
                new List<string?>
                {
                    "ButtonsEnabled",
                    "IsBusy",
                    "IsConnected",
                    "IsStarted",
                    "StatusText",
                    "ToggleButtonText"
                },
                rowRaised);

            managerRaised.Sort(StringComparer.Ordinal);
            Assert.Equal(
                new List<string?> { "AnyConnected", "ConnectedSystemSummary" },
                managerRaised);
            Assert.True(manager.AnyConnected);
            Assert.Equal("Alpha 10.0.0.1:54000", manager.ConnectedSystemSummary);
        }

        /// <summary>
        /// Aggregate observers see a coherent pair: when AnyConnected is
        /// raised, ConnectedSystemSummary has already been updated.
        /// </summary>
        [Fact]
        public void Manager_AggregateNotifications_ExposeAtomicState()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha", address: "10.0.0.1")
                });
            var observedAnyConnected = false;
            string? observedSummary = null;

            manager.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FneConnectionManagerViewModel.AnyConnected))
                {
                    observedAnyConnected = manager.AnyConnected;
                    observedSummary = manager.ConnectedSystemSummary;
                }
            };

            manager.ApplyState("Alpha", isConnected: true, isBusy: false, isStarted: true);

            Assert.True(observedAnyConnected);
            Assert.Equal("Alpha 10.0.0.1:54000", observedSummary);
        }

        /// <summary>
        /// Re-applying identical state raises nothing: the row suppresses
        /// same-value notifications and the aggregates are change-only.
        /// </summary>
        [Fact]
        public void Manager_ApplyState_SameValues_NoNotifications()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha", address: "10.0.0.1") });
            manager.ApplyState("Alpha", isConnected: true, isBusy: false, isStarted: true);
            var row = manager.Systems[0];
            var managerRaised = new List<string?>();
            var rowRaised = new List<string?>();
            manager.PropertyChanged += (_, e) => managerRaised.Add(e.PropertyName);
            row.PropertyChanged += (_, e) => rowRaised.Add(e.PropertyName);

            manager.ApplyState("alpha", isConnected: true, isBusy: false, isStarted: true);

            Assert.True(manager.AnyConnected);
            Assert.Equal("Alpha 10.0.0.1:54000", manager.ConnectedSystemSummary);
            Assert.Empty(managerRaised);
            Assert.Empty(rowRaised);
        }

        /// <summary>
        /// The summary is the FIRST connected row in sorted order; when a
        /// second, earlier-sorted row connects, the summary moves to it and
        /// the aggregate notifies only on actual change.
        /// </summary>
        [Fact]
        public void Manager_ApplyState_ConnectingEarlierRow_MovesSummaryToFirstConnectedSorted()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Zulu", address: "10.0.0.3"),
                    MakeSystem("Alpha", address: "10.0.0.1"),
                    MakeSystem("Mike", address: "10.0.0.2")
                });

            manager.ApplyState("zulu", isConnected: true, isBusy: false, isStarted: true);

            Assert.True(manager.AnyConnected);
            Assert.Equal("Zulu 10.0.0.3:54000", manager.ConnectedSystemSummary);

            var managerRaised = new List<string?>();
            manager.PropertyChanged += (_, e) => managerRaised.Add(e.PropertyName);

            manager.ApplyState("Alpha", isConnected: true, isBusy: false, isStarted: true);

            Assert.True(manager.AnyConnected);
            Assert.Equal("Alpha 10.0.0.1:54000", manager.ConnectedSystemSummary);
            Assert.Equal(new List<string?> { "ConnectedSystemSummary" }, managerRaised);
        }

        /// <summary>
        /// Disconnecting the last connected row clears both aggregates:
        /// AnyConnected falls back to false and the summary to null, with
        /// change-only notifications for both.
        /// </summary>
        [Fact]
        public void Manager_ApplyState_DisconnectingLastConnected_ClearsAggregates()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha", address: "10.0.0.1"),
                    MakeSystem("Beta", address: "10.0.0.2")
                });
            manager.ApplyState("Alpha", isConnected: true, isBusy: false, isStarted: true);
            var managerRaised = new List<string?>();
            manager.PropertyChanged += (_, e) => managerRaised.Add(e.PropertyName);

            manager.ApplyState("alpha", isConnected: false, isBusy: false, isStarted: false);

            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);
            managerRaised.Sort(StringComparer.Ordinal);
            Assert.Equal(
                new List<string?> { "AnyConnected", "ConnectedSystemSummary" },
                managerRaised);
        }

        /// <summary>
        /// ApplyState always assigns the given state verbatim, so a call
        /// with isBusy false clears a busy row (and its derived
        /// ButtonsEnabled follows).
        /// </summary>
        [Fact]
        public void Manager_ApplyState_ClearsBusy()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha") });
            var row = manager.Systems[0];
            manager.StartSystem("Alpha");
            Assert.True(row.IsBusy);
            Assert.False(row.ButtonsEnabled);

            manager.ApplyState("Alpha", isConnected: true, isBusy: false, isStarted: true);

            Assert.False(row.IsBusy);
            Assert.True(row.ButtonsEnabled);
            Assert.True(row.IsConnected);
            Assert.True(row.IsStarted);
        }

        // ---- B. Manager: start / stop / restart requests ---------------------------

        /// <summary>
        /// StartSystem sets the matching row IsBusy=true BEFORE raising
        /// StartRequested exactly once, with the canonical row.SystemName
        /// (the caller's casing is irrelevant).
        /// </summary>
        [Fact]
        public void Manager_StartSystem_SetsBusyThenRaisesOnce_CanonicalName()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha"),
                    MakeSystem("Beta")
                });
            var alpha = manager.Systems[0];
            var requested = new List<string>();
            var busyAtEvent = false;
            manager.StartRequested += name =>
            {
                requested.Add(name);
                busyAtEvent = alpha.IsBusy;
            };

            manager.StartSystem("aLpHa");

            Assert.Equal(new List<string> { "Alpha" }, requested);
            Assert.True(busyAtEvent);
            Assert.True(alpha.IsBusy);
            Assert.False(alpha.ButtonsEnabled);
        }

        /// <summary>
        /// StartSystem is a no-op for unknown or blank system names: no
        /// event, no state change.
        /// </summary>
        [Theory]
        [InlineData("Nope")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Manager_StartSystem_UnknownOrBlank_NoOp(string? systemName)
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha") });
            var row = manager.Systems[0];
            var requested = new List<string>();
            manager.StartRequested += requested.Add;

            manager.StartSystem(systemName!);

            Assert.Empty(requested);
            Assert.False(row.IsBusy);
            Assert.False(row.IsStarted);
        }

        /// <summary>
        /// StartSystem on a busy row is a no-op: no second event, no
        /// further state change.
        /// </summary>
        [Fact]
        public void Manager_StartSystem_BusyRow_NoOp()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha") });
            var row = manager.Systems[0];
            manager.StartSystem("Alpha");
            var requested = new List<string>();
            manager.StartRequested += requested.Add;

            manager.StartSystem("Alpha");

            Assert.Empty(requested);
            Assert.True(row.IsBusy);
        }

        /// <summary>
        /// StopSystem sets the matching row IsBusy=true BEFORE raising
        /// StopRequested exactly once, with the canonical SystemName.
        /// </summary>
        [Fact]
        public void Manager_StopSystem_SetsBusyThenRaisesOnce_CanonicalName()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha"),
                    MakeSystem("Beta")
                });
            var alpha = manager.Systems[0];
            var requested = new List<string>();
            var busyAtEvent = false;
            manager.StopRequested += name =>
            {
                requested.Add(name);
                busyAtEvent = alpha.IsBusy;
            };

            manager.StopSystem("ALPHA");

            Assert.Equal(new List<string> { "Alpha" }, requested);
            Assert.True(busyAtEvent);
            Assert.True(alpha.IsBusy);
            Assert.False(alpha.ButtonsEnabled);
        }

        /// <summary>
        /// RestartSystem sets the matching row IsBusy=true BEFORE raising
        /// RestartRequested exactly once, with the canonical SystemName.
        /// </summary>
        [Fact]
        public void Manager_RestartSystem_SetsBusyThenRaisesOnce_CanonicalName()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?>
                {
                    MakeSystem("Alpha"),
                    MakeSystem("Beta")
                });
            var alpha = manager.Systems[0];
            var requested = new List<string>();
            var busyAtEvent = false;
            manager.RestartRequested += name =>
            {
                requested.Add(name);
                busyAtEvent = alpha.IsBusy;
            };

            manager.RestartSystem("alpha");

            Assert.Equal(new List<string> { "Alpha" }, requested);
            Assert.True(busyAtEvent);
            Assert.True(alpha.IsBusy);
            Assert.False(alpha.ButtonsEnabled);
        }

        /// <summary>
        /// The busy guard applies to all three request methods: while a row
        /// is busy, Stop and Restart are no-ops just like Start.
        /// </summary>
        [Fact]
        public void Manager_BusyGuard_BlocksStopAndRestart()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha") });
            var row = manager.Systems[0];
            manager.StartSystem("Alpha");
            var stopped = new List<string>();
            var restarted = new List<string>();
            manager.StopRequested += stopped.Add;
            manager.RestartRequested += restarted.Add;

            manager.StopSystem("Alpha");
            manager.RestartSystem("Alpha");

            Assert.Empty(stopped);
            Assert.Empty(restarted);
            Assert.True(row.IsBusy);
        }

        /// <summary>
        /// Full lifecycle: start raises the request and marks the row busy;
        /// ApplyState lands the connected state and clears busy; stop
        /// raises its request; disconnecting clears the aggregates; the row
        /// is startable again once ApplyState cleared busy.
        /// </summary>
        [Fact]
        public void Manager_Lifecycle_Start_ApplyConnected_Stop_Restartable()
        {
            var manager = new FneConnectionManagerViewModel(
                new List<Codeplug.System?> { MakeSystem("Alpha", address: "10.0.0.1") });
            var row = manager.Systems[0];
            var started = new List<string>();
            var stopped = new List<string>();
            manager.StartRequested += started.Add;
            manager.StopRequested += stopped.Add;

            manager.StartSystem("Alpha");

            Assert.Equal(new List<string> { "Alpha" }, started);
            Assert.True(row.IsBusy);
            Assert.False(manager.AnyConnected);

            manager.ApplyState("Alpha", isConnected: true, isBusy: false, isStarted: true);

            Assert.True(row.IsConnected);
            Assert.True(row.IsStarted);
            Assert.False(row.IsBusy);
            Assert.True(manager.AnyConnected);
            Assert.Equal("Alpha 10.0.0.1:54000", manager.ConnectedSystemSummary);

            manager.StopSystem("Alpha");

            Assert.Equal(new List<string> { "Alpha" }, stopped);
            Assert.True(row.IsBusy);

            manager.ApplyState("Alpha", isConnected: false, isBusy: false, isStarted: false);

            Assert.False(row.IsConnected);
            Assert.False(row.IsStarted);
            Assert.False(row.IsBusy);
            Assert.False(manager.AnyConnected);
            Assert.Null(manager.ConnectedSystemSummary);

            manager.StartSystem("Alpha");

            Assert.Equal(2, started.Count);
            Assert.True(row.IsBusy);
        }

        // ---- B. Manager: shape -----------------------------------------------------

        /// <summary>
        /// Shape gate for the manager: sealed observable type, exact ctor
        /// set (parameterless and IReadOnlyList&lt;Codeplug.System&gt;?),
        /// exact property types, the ApplyState and request method
        /// signatures, and the three Action&lt;string&gt; events.
        /// </summary>
        [Fact]
        public void ManagerShape_SealedObservable_ExactSignaturesAndEvents()
        {
            var manager = typeof(FneConnectionManagerViewModel);

            Assert.True(manager.IsSealed);
            Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(manager));

            Assert.NotNull(manager.GetConstructor(Type.EmptyTypes));

            var listCtor = manager.GetConstructors();
            Assert.Contains(
                listCtor,
                c => c.GetParameters().Length == 1
                     && c.GetParameters()[0].ParameterType == typeof(IReadOnlyList<Codeplug.System>));

            Assert.Equal(
                typeof(IReadOnlyList<FneSystemConnectionViewModel>),
                manager.GetProperty(nameof(FneConnectionManagerViewModel.Systems))!.PropertyType);
            Assert.Equal(typeof(bool), manager.GetProperty(nameof(FneConnectionManagerViewModel.HasSystems))!.PropertyType);
            Assert.Equal(typeof(bool), manager.GetProperty(nameof(FneConnectionManagerViewModel.HasNoSystems))!.PropertyType);
            Assert.Equal(typeof(bool), manager.GetProperty(nameof(FneConnectionManagerViewModel.AnyConnected))!.PropertyType);
            Assert.Equal(
                typeof(string),
                manager.GetProperty(nameof(FneConnectionManagerViewModel.ConnectedSystemSummary))!.PropertyType);

            var applyState = manager.GetMethod(
                nameof(FneConnectionManagerViewModel.ApplyState),
                new[] { typeof(string), typeof(bool), typeof(bool), typeof(bool) });
            Assert.NotNull(applyState);
            Assert.Equal(typeof(void), applyState!.ReturnType);

            Assert.NotNull(manager.GetMethod(
                nameof(FneConnectionManagerViewModel.StartSystem),
                new[] { typeof(string) }));
            Assert.NotNull(manager.GetMethod(
                nameof(FneConnectionManagerViewModel.StopSystem),
                new[] { typeof(string) }));
            Assert.NotNull(manager.GetMethod(
                nameof(FneConnectionManagerViewModel.RestartSystem),
                new[] { typeof(string) }));

            Assert.Equal(
                typeof(Action<string>),
                manager.GetEvent(nameof(FneConnectionManagerViewModel.StartRequested))!.EventHandlerType);
            Assert.Equal(
                typeof(Action<string>),
                manager.GetEvent(nameof(FneConnectionManagerViewModel.StopRequested))!.EventHandlerType);
            Assert.Equal(
                typeof(Action<string>),
                manager.GetEvent(nameof(FneConnectionManagerViewModel.RestartRequested))!.EventHandlerType);

            // No secrets anywhere on the manager's public row surface either.
            Assert.Null(manager.GetProperty("Password"));
            Assert.Null(manager.GetProperty("PresharedKey"));
            Assert.Null(manager.GetProperty("Rid"));
        }

        // ---- C. MainWindowViewModel integration --------------------------------------

        /// <summary>
        /// MainWindowViewModel exposes a get-only FneConnections property
        /// of the exact manager type, constructed EMPTY by the existing
        /// parameterless constructor, and stable across accesses.
        /// </summary>
        [Fact]
        public void MainWindow_FneConnections_GetOnly_EmptyManager_StableInstance()
        {
            var vm = new MainWindowViewModel();
            FneConnectionManagerViewModel fne = vm.FneConnections; // compile-lock: exact type

            Assert.Same(fne, vm.FneConnections);
            Assert.Empty(fne.Systems);
            Assert.False(fne.HasSystems);
            Assert.True(fne.HasNoSystems);
            Assert.False(fne.AnyConnected);
            Assert.Null(fne.ConnectedSystemSummary);

            var property = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.FneConnections))!;
            Assert.Equal(typeof(FneConnectionManagerViewModel), property.PropertyType);
            Assert.False(property.CanWrite);
        }

        /// <summary>
        /// The existing connection-state semantics are untouched: the
        /// dashboard connection state is independent of the FNE
        /// connection-manager slice, which stays empty and disconnected.
        /// </summary>
        [Fact]
        public void MainWindow_SetConnectionState_DoesNotTouchFneConnections()
        {
            var vm = new MainWindowViewModel();
            var fne = vm.FneConnections;

            vm.SetConnectionState("LINKED", "FNE-7 127.0.0.1:54000", isConnected: true);

            Assert.Equal("LINKED", vm.ConnectionLabel);
            Assert.Equal("FNE-7 127.0.0.1:54000", vm.ConnectionDetail);
            Assert.True(vm.IsConnected);
            Assert.False(vm.CanConnect);

            Assert.Empty(fne.Systems);
            Assert.False(fne.AnyConnected);
            Assert.Null(fne.ConnectedSystemSummary);
        }

        /// <summary>
        /// A seeded manager drives the dashboard header from its aggregate
        /// state: the first connected system makes the shell LINKED with
        /// the canonical summary, and disconnecting the last system returns
        /// it to the honest offline state.
        /// </summary>
        [Fact]
        public void MainWindow_FneAggregate_DrivesDashboardConnectionState()
        {
            var vm = new MainWindowViewModel(
                new List<Codeplug.System>
                {
                    MakeSystem("Alpha", address: "10.0.0.1")
                });

            vm.FneConnections.ApplyState(
                "alpha",
                isConnected: true,
                isBusy: false,
                isStarted: true);

            Assert.Equal("LINKED", vm.ConnectionLabel);
            Assert.Equal("Alpha 10.0.0.1:54000", vm.ConnectionDetail);
            Assert.True(vm.IsConnected);
            Assert.False(vm.CanConnect);

            vm.FneConnections.ApplyState(
                "ALPHA",
                isConnected: false,
                isBusy: false,
                isStarted: false);

            Assert.Equal("OFFLINE", vm.ConnectionLabel);
            Assert.Equal("Awaiting FNE configuration", vm.ConnectionDetail);
            Assert.False(vm.IsConnected);
            Assert.True(vm.CanConnect);
        }
    }
}
