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
* GREEN contract gate for the WPF adapter: the live, non-generic
* dvmconsole.SelectedChannelsManager (dvmconsole/SelectedChannelsManager.cs),
* used as the parity baseline for the portable generic
* SelectedChannelsManager<T> implemented in DvmConsole.Core/Selection/. These
* tests lock the WPF manager's behavior and exact event ordering against the
* contract the generic port was transcribed from, so the port cannot drift
* from the production WPF behavior it replaces.
*
* WPF RUNTIME EXECUTION IS WINDOWS-ONLY. This project compiles on Linux via
* EnableWindowsTargeting and the Windows targeting pack, but every test
* constructs real ChannelBox controls on an STA thread, so execution requires
* the Windows WPF runtime and is not required on Linux. Nothing here touches native
* code, files, or secrets; channel identity is reference-based, exactly like
* the WPF source.
*
* Expected state against the pre-adapter implementation (commit 191efd3):
*   GREEN - default primary null; Add/Remove/Clear event trace and exact
*           ordering; primary cleared before the selection events when the
*           removed member is primary; ClearSelections leaves the primary
*           channel untouched and raises no PrimaryChannelChanged.
*   GREEN - GetSelectedChannels returns a detached snapshot and two calls
*           return distinct instances after the Core adapter is wired.
*   GREEN - SetPrimaryChannel(null) throws ArgumentNullException before the
*           Log.WriteLine delegate is reached after the Core adapter is wired.
*/
#nullable disable
using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using dvmconsole;
using dvmconsole.Controls;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;
using Xunit;

namespace DvmConsole.Wpf.Tests
{
    [CollectionDefinition("WPF adapter", DisableParallelization = true)]
    public sealed class WpfAdapterCollection : ICollectionFixture<WpfTestHost>
    {
    }

    /// <summary>
    /// Owns one WPF dispatcher for the test collection. ChannelBox keeps
    /// dispatcher-affine static brushes and borders, so every test must run
    /// on the same STA as the first ChannelBox construction.
    /// </summary>
    public sealed class WpfTestHost : IDisposable
    {
        private readonly ManualResetEventSlim _started = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private Dispatcher _dispatcher;
        private Exception _startupFailure;

        public WpfTestHost()
        {
            _thread = new Thread(RunDispatcher)
            {
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.Wait();

            if (_startupFailure != null)
                throw new InvalidOperationException("The WPF test dispatcher could not start.", _startupFailure);
        }

        public void Run(Action action)
        {
            _dispatcher.Invoke(action);
        }

        private void RunDispatcher()
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
                application.Resources.MergedDictionaries.Add(new BundledTheme
                {
                    BaseTheme = BaseTheme.Light,
                    PrimaryColor = PrimaryColor.Blue,
                    SecondaryColor = SecondaryColor.Green,
                });
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign2.Defaults.xaml",
                        UriKind.Absolute),
                });

                if (dvmconsole.SettingsManager.Instance == null)
                    _ = new dvmconsole.SettingsManager();

                _started.Set();
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                _startupFailure = exception;
                _started.Set();
            }
        }

        public void Dispose()
        {
            if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
            {
                _dispatcher.InvokeShutdown();
                _thread.Join();
            }

            _started.Dispose();
        }
    }

    /// <summary>
    /// Contract tests for the non-generic WPF
    /// <see cref="SelectedChannelsManager"/> adapter.
    /// </summary>
    [Collection("WPF adapter")]
    public class SelectedChannelsManagerAdapterTests
    {
        private readonly WpfTestHost _host;

        public SelectedChannelsManagerAdapterTests(WpfTestHost host)
        {
            _host = host;
        }

        private void RunOnWpf(Action action)
        {
            _host.Run(action);
        }

        /// <summary>
        /// Creates a real ChannelBox through its public constructor. Every
        /// test runs through the shared WPF dispatcher host, which installs the
        /// same application-level MaterialDesign resources and SettingsManager
        /// singleton used by the production App before the control is constructed.
        /// Null manager and audio arguments are safe for this state-only gate;
        /// runtime remains Windows-only and no native code or secrets are involved.
        /// </summary>
        private static ChannelBox CreateChannel(string name)
        {
            return new ChannelBox(null, null, name, "TEST_SYSTEM", "100");
        }

        /// <summary>
        /// Creates a manager wired to a single ordered event trace so tests
        /// can lock the exact event ordering of each operation.
        /// </summary>
        private static (SelectedChannelsManager Manager, List<string> Trace) CreateManager()
        {
            var trace = new List<string>();
            var manager = new SelectedChannelsManager();
            manager.SelectedChannelsChanged += () => trace.Add("SelectedChannelsChanged");
            manager.PrimaryChannelChanged += () => trace.Add("PrimaryChannelChanged");
            manager.ChannelSelectionChanged += (channel, selected) => trace.Add($"ChannelSelectionChanged({channel.ChannelName}, {selected})");
            return (manager, trace);
        }

        /*
        ** Parity: default state
        */

        /// <summary>
        /// A fresh manager has no primary channel (and an empty selection).
        /// </summary>
        [Fact]
        public void Ctor_Default_PrimaryChannelNull()
        {
            RunOnWpf(() =>
            {
                var manager = new SelectedChannelsManager();

                Assert.Null(manager.PrimaryChannel);
                Assert.Empty(manager.GetSelectedChannels());
            });
        }

        /*
        ** Parity: Add / Remove / Clear event trace and exact order
        */

        /// <summary>
        /// Adding a new channel marks it selected, then raises
        /// ChannelSelectionChanged(channel, true) before
        /// SelectedChannelsChanged, exactly once each.
        /// </summary>
        [Fact]
        public void AddSelectedChannel_NewChannel_ExactEventOrder()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var channel = CreateChannel("A");

                manager.AddSelectedChannel(channel);

                Assert.True(channel.IsSelected);
                Assert.Equal(new[]
                {
                    "ChannelSelectionChanged(A, True)",
                    "SelectedChannelsChanged",
                }, trace);
            });
        }

        /// <summary>
        /// Adding an already-selected channel is a full no-op: no events at
        /// all and no membership change.
        /// </summary>
        [Fact]
        public void AddSelectedChannel_Duplicate_IsNoOp()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var channel = CreateChannel("A");
                manager.AddSelectedChannel(channel);
                trace.Clear();

                manager.AddSelectedChannel(channel);

                Assert.Empty(trace);
                Assert.Single(manager.GetSelectedChannels());
            });
        }

        /// <summary>
        /// Removing a non-primary channel unmarks it and raises
        /// ChannelSelectionChanged(channel, false) before
        /// SelectedChannelsChanged, exactly once each.
        /// </summary>
        [Fact]
        public void RemoveSelectedChannel_NonPrimary_ExactEventOrder()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var channel = CreateChannel("A");
                manager.AddSelectedChannel(channel);
                trace.Clear();

                manager.RemoveSelectedChannel(channel);

                Assert.False(channel.IsSelected);
                Assert.Empty(manager.GetSelectedChannels());
                Assert.Equal(new[]
                {
                    "ChannelSelectionChanged(A, False)",
                    "SelectedChannelsChanged",
                }, trace);
            });
        }

        /// <summary>
        /// Removing a channel that is not selected is a full no-op: no
        /// events and the existing members stay untouched.
        /// </summary>
        [Fact]
        public void RemoveSelectedChannel_NonMember_IsNoOp()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var member = CreateChannel("A");
                var outsider = CreateChannel("B");
                manager.AddSelectedChannel(member);
                trace.Clear();

                manager.RemoveSelectedChannel(outsider);

                Assert.Empty(trace);
                Assert.Single(manager.GetSelectedChannels());
            });
        }

        /// <summary>
        /// Removing the primary member clears it FIRST: PrimaryChannelChanged
        /// fires before the selection events, and PrimaryChannel is already
        /// null when ChannelSelectionChanged is raised.
        /// </summary>
        [Fact]
        public void RemoveSelectedChannel_Primary_ClearsPrimaryBeforeSelectionEvents()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var channel = CreateChannel("A");
                manager.SetPrimaryChannel(channel);
                manager.AddSelectedChannel(channel);
                trace.Clear();
                ChannelBox primaryAtSelectionEvent = null;
                manager.ChannelSelectionChanged += (c, s) => primaryAtSelectionEvent = manager.PrimaryChannel;

                manager.RemoveSelectedChannel(channel);

                Assert.Null(primaryAtSelectionEvent);
                Assert.Null(manager.PrimaryChannel);
                Assert.Empty(manager.GetSelectedChannels());
                Assert.Equal(new[]
                {
                    "PrimaryChannelChanged",
                    "ChannelSelectionChanged(A, False)",
                    "SelectedChannelsChanged",
                }, trace);
            });
        }

        /// <summary>
        /// Clear raises one ChannelSelectionChanged(channel, false) per
        /// member, then a single SelectedChannelsChanged last, and unmarks
        /// every member.
        /// </summary>
        [Fact]
        public void ClearSelections_PerChannelEventsThenSingleChanged()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var a = CreateChannel("A");
                var b = CreateChannel("B");
                manager.AddSelectedChannel(a);
                manager.AddSelectedChannel(b);
                trace.Clear();

                manager.ClearSelections();

                Assert.False(a.IsSelected);
                Assert.False(b.IsSelected);
                var selectionEvents = trace
                    .Where(t => t.StartsWith("ChannelSelectionChanged", StringComparison.Ordinal))
                    .ToList();
                Assert.Equal(2, selectionEvents.Count);
                Assert.All(selectionEvents, e => Assert.EndsWith(", False)", e));
                Assert.Equal(2, selectionEvents.Distinct().Count());
                Assert.Equal("SelectedChannelsChanged", trace.Last());
                Assert.Equal(3, trace.Count);
            });
        }

        /// <summary>
        /// ClearSelections leaves the primary channel untouched and raises
        /// no PrimaryChannelChanged (explicit compatibility quirk of the
        /// WPF source, locked as behavior).
        /// </summary>
        [Fact]
        public void ClearSelections_LeavesPrimary_NoPrimaryEvent()
        {
            RunOnWpf(() =>
            {
                var (manager, trace) = CreateManager();
                var channel = CreateChannel("A");
                manager.SetPrimaryChannel(channel);
                manager.AddSelectedChannel(channel);
                trace.Clear();

                manager.ClearSelections();

                Assert.Same(channel, manager.PrimaryChannel);
                Assert.Empty(manager.GetSelectedChannels());
                Assert.DoesNotContain("PrimaryChannelChanged", trace);
            });
        }

        /*
        ** Expected RED on the current WPF implementation
        */

        /// <summary>
        /// GetSelectedChannels must return a detached snapshot: mutating the
        /// returned collection must not affect the manager.
        /// This was RED against the pre-adapter implementation, which
        /// returned the live internal HashSet; the Core adapter is GREEN.
        /// </summary>
        [Fact]
        public void GetSelectedChannels_ReturnsDetachedSnapshot()
        {
            RunOnWpf(() =>
            {
                var (manager, _) = CreateManager();
                var a = CreateChannel("A");
                var b = CreateChannel("B");
                manager.AddSelectedChannel(a);
                manager.AddSelectedChannel(b);

                var snapshot = manager.GetSelectedChannels();
                try
                {
                    (snapshot as ICollection<ChannelBox>)?.Clear();
                }
                catch (NotSupportedException)
                {
                    // A future detached snapshot may be read-only; that is the contract.
                }

                Assert.Equal(2, manager.GetSelectedChannels().Count);
            });
        }

        /// <summary>
        /// Two calls to GetSelectedChannels must return distinct collection
        /// instances, never the same live object.
        /// This was RED against the pre-adapter implementation, which
        /// returned the same internal HashSet instance on every call.
        /// </summary>
        [Fact]
        public void GetSelectedChannels_TwoCalls_DistinctInstances()
        {
            RunOnWpf(() =>
            {
                var (manager, _) = CreateManager();
                manager.AddSelectedChannel(CreateChannel("A"));

                var first = manager.GetSelectedChannels();
                var second = manager.GetSelectedChannels();

                Assert.NotSame(first, second);
            });
        }

        /// <summary>
        /// SetPrimaryChannel must reject null with ArgumentNullException,
        /// leave the primary untouched, and raise no events.
        /// This was RED against the pre-adapter implementation, which
        /// dereferenced channel.ChannelName inside Log.WriteLine first.
        /// </summary>
        [Fact]
        public void SetPrimaryChannel_Null_ThrowsArgumentNullException()
        {
            RunOnWpf(() =>
            {
                var manager = new SelectedChannelsManager();
                var primaryEvents = 0;
                manager.PrimaryChannelChanged += () => primaryEvents++;

                Assert.Throws<ArgumentNullException>(() => manager.SetPrimaryChannel(null));

                Assert.Null(manager.PrimaryChannel);
                Assert.Equal(0, primaryEvents);
            });
        }
    } // public class SelectedChannelsManagerAdapterTests
} // namespace DvmConsole.Wpf.Tests
