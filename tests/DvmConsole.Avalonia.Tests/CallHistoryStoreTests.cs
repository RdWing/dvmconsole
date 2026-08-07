// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the Call History slice (audit deleg_79328deb
* READY) — DvmConsole.Avalonia.Services.CallHistoryStore:
*
*   - Sealed; ctor CallHistoryStore(int maxCallHistory = 100,
*     Func<string, uint, bool>? suppress = null); cap clamped [5,100]
*     (WPF CallHistoryWindow.xaml.cs:112,150-153).
*   - AddFrame(ReceivedCallMetadata m, string? channelName): one entry
*     per call stream per key — dedup by StreamId per key, terminator
*     clears the key's stream (WPF isNewCallStream parity,
*     MainWindow.DMR.cs:335); newest-first; evict oldest at cap
*     (WPF :270-281); suppression delegate consulted when non-null
*     (isConsoleRid parity, MainWindow.Tar.cs:177-180).
*   - Entries: immutable CallHistoryEntry (Key, ChannelName,
*     SystemName, SrcId, DstId, Alias, Mode, Timestamp DateTimeOffset).
*   - IReadOnlyList<CallHistoryEntry> Entries (snapshot) + event
*     Action? Changed (raised on any mutation). Lock-guarded; no UI.
*/
using System;
using System.Collections.Generic;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="CallHistoryStore"/>.
    /// </summary>
    public sealed class CallHistoryStoreTests
    {
        /* ------------------------------------------------------------------
        ** Surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ApiShape_ExactPublicSurface()
        {
            var type = typeof(CallHistoryStore);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(int), typeof(Func<string, uint, bool>),
            }));
            Assert.Equal(
                typeof(IReadOnlyList<CallHistoryEntry>),
                type.GetProperty(nameof(CallHistoryStore.Entries))!.PropertyType);
            Assert.NotNull(type.GetEvent(nameof(CallHistoryStore.Changed)));
            Assert.NotNull(type.GetMethod(nameof(CallHistoryStore.AddFrame), new[]
            {
                typeof(ReceivedCallMetadata), typeof(string),
            }));
        }

        [Fact]
        public void Entry_ExactSurface()
        {
            var type = typeof(CallHistoryEntry);
            Assert.True(type.IsSealed);
            foreach (var prop in new[]
            {
                nameof(CallHistoryEntry.Key), nameof(CallHistoryEntry.ChannelName),
                nameof(CallHistoryEntry.SystemName), nameof(CallHistoryEntry.Alias),
            })
            {
                Assert.Equal(typeof(string), type.GetProperty(prop)!.PropertyType);
                Assert.False(type.GetProperty(prop)!.CanWrite);
            }
            Assert.Equal(typeof(uint), type.GetProperty(nameof(CallHistoryEntry.SrcId))!.PropertyType);
            Assert.Equal(typeof(uint), type.GetProperty(nameof(CallHistoryEntry.DstId))!.PropertyType);
            Assert.Equal(typeof(VoiceMode), type.GetProperty(nameof(CallHistoryEntry.Mode))!.PropertyType);
            Assert.Equal(typeof(DateTimeOffset), type.GetProperty(nameof(CallHistoryEntry.Timestamp))!.PropertyType);
            Assert.False(type.GetProperty(nameof(CallHistoryEntry.SrcId))!.CanWrite);
            Assert.False(type.GetProperty(nameof(CallHistoryEntry.DstId))!.CanWrite);
            Assert.False(type.GetProperty(nameof(CallHistoryEntry.Mode))!.CanWrite);
            Assert.False(type.GetProperty(nameof(CallHistoryEntry.Timestamp))!.CanWrite);
        }

        /* ------------------------------------------------------------------
        ** Fixture helpers
        ** ---------------------------------------------------------------- */

        private static ReceivedCallMetadata Voice(string key, uint src, uint dst, uint stream, byte slot = 1)
            => new ReceivedCallMetadata("System 1", src, dst, slot, VoiceMode.Dmr, stream, key, IsTerminator: false);

        private static ReceivedCallMetadata Term(string key, uint src, uint dst, uint stream, byte slot = 1)
            => new ReceivedCallMetadata("System 1", src, dst, slot, VoiceMode.Dmr, stream, key, IsTerminator: true);

        /* ------------------------------------------------------------------
        ** Cap and eviction
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Cap_ClampedToRange()
        {
            // Behavioral clamp: eviction happens at the clamped cap.
            var small = new CallHistoryStore(1); // clamps to 5
            for (uint i = 1; i <= 7; i++)
            {
                small.AddFrame(Voice("k", 1000 + i, 2000, i), "CH " + i);
            }
            Assert.Equal(5, small.Entries.Count);

            var large = new CallHistoryStore(500); // clamps to 100
            Assert.Empty(large.Entries); // default cap only bites on eviction

            var def = new CallHistoryStore();
            Assert.Empty(def.Entries);
        }

        [Fact]
        public void Eviction_NewestFirst_OldestDroppedAtCap()
        {
            var store = new CallHistoryStore(5);
            for (uint i = 1; i <= 7; i++)
            {
                store.AddFrame(Voice("k", 1000 + i, 2000, i), "CH " + i);
            }

            Assert.Equal(5, store.Entries.Count);
            Assert.Equal("CH 7", store.Entries[0].ChannelName); // newest first
            Assert.Equal("CH 3", store.Entries[4].ChannelName); // oldest kept
        }

        /* ------------------------------------------------------------------
        ** Dedup by stream
        ** ---------------------------------------------------------------- */

        [Fact]
        public void SameStream_NoNewEntry()
        {
            var store = new CallHistoryStore();
            store.AddFrame(Voice("k", 100, 200, 42), "CH 1");
            store.AddFrame(Voice("k", 100, 200, 42), "CH 1"); // same stream

            Assert.Single(store.Entries);
        }

        [Fact]
        public void NewStream_NewEntry()
        {
            var store = new CallHistoryStore();
            store.AddFrame(Voice("k", 100, 200, 42), "CH 1");
            store.AddFrame(Voice("k", 100, 200, 43), "CH 1"); // new stream

            Assert.Equal(2, store.Entries.Count);
        }

        [Fact]
        public void Terminator_ClearsStream_SoReusedStreamIdRecords()
        {
            var store = new CallHistoryStore();
            store.AddFrame(Voice("k", 100, 200, 42), "CH 1");
            store.AddFrame(Term("k", 100, 200, 42), "CH 1"); // clears stream
            store.AddFrame(Voice("k", 100, 200, 42), "CH 1"); // same id, new call

            Assert.Equal(2, store.Entries.Count); // both calls recorded
        }

        [Fact]
        public void Terminator_ItselfNotRecorded()
        {
            var store = new CallHistoryStore();
            store.AddFrame(Term("k", 100, 200, 42), "CH 1");

            Assert.Empty(store.Entries);
        }

        /* ------------------------------------------------------------------
        ** Per-key stream tracking
        ** ---------------------------------------------------------------- */

        [Fact]
        public void StreamsTrackedPerKey()
        {
            var store = new CallHistoryStore();
            store.AddFrame(Voice("a", 100, 200, 42), "CH 1");
            store.AddFrame(Voice("b", 100, 300, 42), "CH 2"); // same streamId, other key

            Assert.Equal(2, store.Entries.Count);
            store.AddFrame(Voice("a", 100, 200, 43), "CH 1"); // new stream on key a
            Assert.Equal(3, store.Entries.Count);
        }

        /* ------------------------------------------------------------------
        ** Suppression
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Suppressed_NotRecorded()
        {
            bool suppressed = false;
            var store = new CallHistoryStore(100, (system, src) =>
            {
                suppressed = true;
                return src == 9000; // console RID parity
            });

            store.AddFrame(Voice("k", 9000, 200, 1), "CH 1");
            store.AddFrame(Voice("k", 100, 200, 2), "CH 1");

            Assert.True(suppressed);
            Assert.Single(store.Entries);
            Assert.Equal(100u, store.Entries[0].SrcId);
        }

        [Fact]
        public void SuppressedTerminator_ClearsStreamState()
        {
            var suppressNext = false;
            var store = new CallHistoryStore(100, (_, _) => suppressNext);

            store.AddFrame(Voice("k", 100, 200, 1), "CH 1");
            suppressNext = true;
            store.AddFrame(Term("k", 100, 200, 1), "CH 1");
            suppressNext = false;
            store.AddFrame(Voice("k", 100, 200, 1), "CH 1");

            Assert.Equal(2, store.Entries.Count);
        }

        [Fact]
        public void NullSuppress_RecordsEverything()
        {
            var store = new CallHistoryStore();
            store.AddFrame(Voice("k", 9000, 200, 1), "CH 1");

            Assert.Single(store.Entries);
        }

        /* ------------------------------------------------------------------
        ** Changed event + entry content
        /* ---------------------------------------------------------------- */

        [Fact]
        public void Changed_RaisedOnMutation_NotOnNoOp()
        {
            var store = new CallHistoryStore();
            int changes = 0;
            store.Changed += () => changes++;

            store.AddFrame(Voice("k", 100, 200, 42), "CH 1");
            store.AddFrame(Voice("k", 100, 200, 42), "CH 1"); // dedup: no mutation

            Assert.Equal(1, changes);
        }

        [Fact]
        public void Entry_CarriesAllMetadata()
        {
            var store = new CallHistoryStore();
            var stamp = new DateTimeOffset(2026, 8, 6, 20, 0, 0, TimeSpan.Zero);
            store.AddFrame(
                new ReceivedCallMetadata("Sys", 111, 222, 2, VoiceMode.P25, 7, "sys|222", IsTerminator: false),
                "CH P25");

            var entry = store.Entries[0];
            Assert.Equal("sys|222", entry.Key);
            Assert.Equal("CH P25", entry.ChannelName);
            Assert.Equal("Sys", entry.SystemName);
            Assert.Equal(111u, entry.SrcId);
            Assert.Equal(222u, entry.DstId);
            Assert.Equal(VoiceMode.P25, entry.Mode);
            Assert.Equal("", entry.Alias);
            Assert.True(entry.Timestamp >= stamp);
        }

        /* ------------------------------------------------------------------
        ** Alias resolution (alias.yml follow-on, additive seam)
        /* ---------------------------------------------------------------- */

        [Fact]
        public void SetAliasResolver_ExactSurface()
        {
            var type = typeof(CallHistoryStore);
            Assert.NotNull(type.GetMethod(nameof(CallHistoryStore.SetAliasResolver), new[]
            {
                typeof(Func<string, uint, string>),
            }));
        }

        [Fact]
        public void AliasResolver_AppliedAtRecordTime()
        {
            var store = new CallHistoryStore();
            store.SetAliasResolver((system, src) =>
                system == "System 1" && src == 111 ? "Alpha Base" : string.Empty);

            store.AddFrame(Voice("k", 111, 222, 1), "CH 1");

            Assert.Equal("Alpha Base", store.Entries[0].Alias);
        }

        [Fact]
        public void AliasResolver_NullResult_StoresEmpty()
        {
            var store = new CallHistoryStore();
            store.SetAliasResolver((_, _) => null!);

            store.AddFrame(Voice("k", 111, 222, 1), "CH 1");

            Assert.Equal(string.Empty, store.Entries[0].Alias);
        }

        [Fact]
        public void AliasResolver_NeverSet_StoresEmpty()
        {
            var store = new CallHistoryStore();

            store.AddFrame(Voice("k", 111, 222, 1), "CH 1");

            Assert.Equal(string.Empty, store.Entries[0].Alias);
        }

        [Fact]
        public void AliasResolver_ClearedAfterNull_StoresEmpty()
        {
            var store = new CallHistoryStore();
            store.SetAliasResolver((_, _) => "Bravo");
            store.SetAliasResolver(null!);

            store.AddFrame(Voice("k", 111, 222, 1), "CH 1");

            Assert.Equal(string.Empty, store.Entries[0].Alias);
        }

        [Fact]
        public void AliasResolver_ThrowFallsBackWithoutCorruptingState()
        {
            var store = new CallHistoryStore();
            store.SetAliasResolver((_, _) => throw new InvalidOperationException("resolver failed"));

            store.AddFrame(Voice("k", 111, 222, 1), "CH 1");

            Assert.Single(store.Entries);
            Assert.Equal(string.Empty, store.Entries[0].Alias);

            store.SetAliasResolver((_, _) => "Recovered");
            store.AddFrame(Voice("k", 111, 222, 2), "CH 1");

            Assert.Equal("Recovered", store.Entries[0].Alias);
        }
    }
}
