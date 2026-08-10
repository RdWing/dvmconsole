// SPDX-License-Identifier: AGPL-3.0-only
/**
* RED contract gate for the headless Core patch forwarding engine.
*/
using Xunit;
using dvmconsole;

namespace DvmConsole.Core.Tests
{
    public sealed class PatchManagerContractTests
    {
        [Fact]
        public void HandleCallStart_NormalizesMemberIdentityAndForwardsToEachOtherMember()
        {
            List<string> begins = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, sourceId) =>
                {
                    begins.Add($"{systemName}|{tgid}|{sourceId}");
                    return 501;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700,
                utcNow: () => new DateTime(2026, 8, 9, 18, 0, 0, DateTimeKind.Utc));

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Group"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "System", Tgid = "123" },
                        new PatchTalkgroupMember { SystemName = " system ", Tgid = " 123 " },
                        new PatchTalkgroupMember { SystemName = "Target", Tgid = "456" }
                    }
                });

            manager.HandleCallStart(" SYSTEM ", " 123 ", 42, 99);

            Assert.Equal(new[] { "Target|456|700" }, begins);
        }

        [Fact]
        public void HandleCallStart_ResolvesFallbackSourceIdForEachForwardTarget()
        {
            List<string> fallbackLookups = new List<string>();
            List<string> begins = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, sourceId) =>
                {
                    begins.Add($"{systemName}|{tgid}|{sourceId}");
                    return 501;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (systemName, tgid) =>
                {
                    fallbackLookups.Add($"{systemName}|{tgid}");
                    return tgid == "200" ? 1200u : 1300u;
                });

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Group"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "Source", Tgid = "100" },
                        new PatchTalkgroupMember { SystemName = "TargetA", Tgid = "200" },
                        new PatchTalkgroupMember { SystemName = "TargetB", Tgid = "300" }
                    }
                });

            manager.HandleCallStart("Source", "100", 42, 99);

            Assert.Equal(new[] { "TargetA|200", "TargetB|300" }, fallbackLookups);
            Assert.Equal(new[] { "TargetA|200|1200", "TargetB|300|1300" }, begins);
        }

        [Fact]
        public void HandleCallStart_OneWayModeAllowsOnlyTheFirstMemberAsSource()
        {
            List<string> begins = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, sourceId) =>
                {
                    begins.Add($"{systemName}|{tgid}|{sourceId}");
                    return 501;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Group"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "Source", Tgid = "100" },
                        new PatchTalkgroupMember { SystemName = "Target", Tgid = "200" }
                    }
                },
                new Dictionary<string, bool> { ["group"] = true });

            manager.HandleCallStart("Target", "200", 42, 99);
            Assert.Empty(begins);

            manager.HandleCallStart("Source", "100", 43, 99);
            Assert.Equal(new[] { "Target|200|700" }, begins);
        }

        [Fact]
        public void HandleCallEnd_EndsEachSuccessfulForwardAndClearsTheActiveSource()
        {
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Group"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "Source", Tgid = "100" },
                        new PatchTalkgroupMember { SystemName = "Target", Tgid = "200" }
                    }
                });

            manager.HandleCallStart("Source", "100", 42, 99);
            manager.HandleCallEnd("Source", "100", 42);
            manager.HandleCallEnd("Source", "100", 42);

            Assert.Equal(new[] { "Target|200|501|700" }, ends);
        }

        [Fact]
        public void ApplyMemberships_IdenticalReapplyPreservesActiveForwarding()
        {
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 99);
            manager.ApplyMemberships(CreateBasicMemberships());

            manager.HandleCallEnd("Source", "100", 42);

            Assert.Equal(new[] { "Target|200|501|700" }, ends);
        }

        [Fact]
        public void ApplyMemberships_RemovingActiveGroupEndsItsForwarding()
        {
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 99);
            manager.ApplyMemberships(new Dictionary<string, List<PatchTalkgroupMember>>());

            Assert.Equal(new[] { "Target|200|501|700" }, ends);
        }

        [Fact]
        public void HandleAudio_ForwardsPcmToEachActiveTargetWithStoredSourceIds()
        {
            List<string> sends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (systemName, tgid, pcm, sourceId) =>
                    sends.Add($"{systemName}|{tgid}|{sourceId}|{Convert.ToHexString(pcm)}"),
                getFallbackSourceId: (_, tgid) => tgid == "200" ? 1200u : 1300u);

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Group"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "Source", Tgid = "100" },
                        new PatchTalkgroupMember { SystemName = "TargetA", Tgid = "200" },
                        new PatchTalkgroupMember { SystemName = "TargetB", Tgid = "300" }
                    }
                });

            manager.HandleCallStart("Source", "100", 42, 99);
            manager.HandleAudio("Source", "100", 42, 99, new byte[] { 0x01, 0xA2 });

            Assert.Equal(
                new[] { "TargetA|200|1200|01A2", "TargetB|300|1300|01A2" },
                sends);
        }

        [Fact]
        public void HandleCallStart_ReplacesStaleSourceAndRestartsForwarding()
        {
            DateTime now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            List<string> begins = new List<string>();
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, sourceId) =>
                {
                    begins.Add($"{systemName}|{tgid}|{sourceId}");
                    return begins.Count switch
                    {
                        1 => 501u,
                        2 => 502u,
                        3 => 601u,
                        _ => 602u
                    };
                },
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700,
                utcNow: () => now);

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["Group"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "SourceA", Tgid = "100" },
                        new PatchTalkgroupMember { SystemName = "SourceB", Tgid = "101" },
                        new PatchTalkgroupMember { SystemName = "Target", Tgid = "200" }
                    }
                });

            manager.HandleCallStart("SourceA", "100", 42, 99);
            now = now.AddMilliseconds(2001);
            manager.HandleCallStart("SourceB", "101", 43, 98);

            Assert.Equal(
                new[]
                {
                    "SourceB|101|700", "Target|200|700",
                    "SourceA|100|700", "Target|200|700"
                },
                begins);
            Assert.Equal(
                new[] { "SourceB|101|501|700", "Target|200|502|700" },
                ends);
        }

        [Fact]
        public void CleanupStaleSources_EndsQuietForwardingExactlyOnce()
        {
            DateTime now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700,
                utcNow: () => now);

            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 99);
            now = now.AddMilliseconds(2001);

            Assert.Equal(1, manager.CleanupStaleSources());
            Assert.Equal(0, manager.CleanupStaleSources());
            Assert.Equal(new[] { "Target|200|501|700" }, ends);
        }

        [Fact]
        public void HandleAudio_RefreshesActivityForStaleCleanup()
        {
            DateTime now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700,
                utcNow: () => now);

            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 99);
            now = now.AddMilliseconds(1500);
            manager.HandleAudio("Source", "100", 42, 99, new byte[] { 0x01 });
            now = now.AddMilliseconds(1000);

            Assert.Equal(0, manager.CleanupStaleSources());
            Assert.Empty(ends);

            now = now.AddMilliseconds(2001);
            Assert.Equal(1, manager.CleanupStaleSources());
            Assert.Equal(new[] { "Target|200|501|700" }, ends);
        }

        [Fact]
        public void SetSourceIdPassthrough_UsesInboundSourceIdForNewForward()
        {
            List<string> begins = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, sourceId) =>
                {
                    begins.Add($"{systemName}|{tgid}|{sourceId}");
                    return 501;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.SetSourceIdPassthrough(true);
            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 1234);

            Assert.Equal(new[] { "Target|200|1234" }, begins);
        }

        [Fact]
        public void HandleAudio_LatchesFirstNonzeroSourceIdForActiveForward()
        {
            List<string> sends = new List<string>();
            List<string> ends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (_, _, sourceId) =>
                {
                    Assert.Equal(700u, sourceId);
                    return 501;
                },
                endForward: (systemName, tgid, streamId, sourceId) =>
                    ends.Add($"{systemName}|{tgid}|{streamId}|{sourceId}"),
                sendForwardAudio: (systemName, tgid, _, sourceId) =>
                    sends.Add($"{systemName}|{tgid}|{sourceId}"),
                getFallbackSourceId: (_, _) => 700);

            manager.SetSourceIdPassthrough(true);
            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 0);

            manager.HandleAudio("Source", "100", 42, 1234, new byte[] { 0x01 });
            manager.HandleAudio("Source", "100", 42, 5678, new byte[] { 0x02 });
            manager.HandleCallEnd("Source", "100", 42);

            Assert.Equal(new[] { "Target|200|1234", "Target|200|1234" }, sends);
            Assert.Equal(new[] { "Target|200|501|1234" }, ends);
        }

        [Fact]
        public void HandleAudio_StartsMissingTargetWithLatchedSourceId()
        {
            List<string> begins = new List<string>();
            List<string> sends = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, sourceId) =>
                {
                    begins.Add($"{systemName}|{tgid}|{sourceId}");
                    return begins.Count == 1 ? 0u : 601u;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (systemName, tgid, pcm, sourceId) =>
                    sends.Add($"{systemName}|{tgid}|{sourceId}|{Convert.ToHexString(pcm)}"),
                getFallbackSourceId: (_, _) => 700);

            manager.SetSourceIdPassthrough(true);
            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 0);

            manager.HandleAudio("Source", "100", 42, 1234, new byte[] { 0x01 });
            manager.HandleAudio("Source", "100", 42, 5678, new byte[] { 0xA2 });

            Assert.Equal(new[] { "Target|200|700", "Target|200|1234" }, begins);
            Assert.Equal(new[] { "Target|200|1234|A2" }, sends);
        }

        [Fact]
        public void IsForwardTargetActive_TracksAcceptedTargetUntilTeardown()
        {
            PatchManager manager = new PatchManager(
                beginForward: (_, _, _) => 501,
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 99);

            Assert.True(manager.IsForwardTargetActive(" Target ", "200"));
            Assert.False(manager.IsForwardTargetActive("Source", "100"));

            manager.HandleCallEnd("Source", "100", 42);

            Assert.False(manager.IsForwardTargetActive("Target", "200"));
        }

        [Fact]
        public void IsPatchedTransmitStream_SuppressesActiveOutboundStreamAsInboundSource()
        {
            List<string> begins = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, _) =>
                {
                    begins.Add($"{systemName}|{tgid}");
                    return 501;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700);

            manager.ApplyMemberships(
                new Dictionary<string, List<PatchTalkgroupMember>>
                {
                    ["GroupA"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "Source", Tgid = "100" },
                        new PatchTalkgroupMember { SystemName = "Target", Tgid = "200" }
                    },
                    ["GroupB"] = new List<PatchTalkgroupMember>
                    {
                        new PatchTalkgroupMember { SystemName = "Target", Tgid = "200" },
                        new PatchTalkgroupMember { SystemName = "Sink", Tgid = "300" }
                    }
                });

            manager.HandleCallStart("Source", "100", 42, 99);

            Assert.Equal(new[] { "Target|200" }, begins);
            Assert.True(manager.IsPatchedTransmitStream(" Target ", "200", 501));
            Assert.False(manager.IsPatchedTransmitStream("Target", "200", 999));

            manager.HandleCallStart("Target", "200", 501, 99);

            Assert.Equal(new[] { "Target|200" }, begins);
        }

        [Fact]
        public void IsPatchedTransmitStream_SuppressesRecentlyEndedStreamUntilExpiry()
        {
            DateTime now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            List<string> begins = new List<string>();
            PatchManager manager = new PatchManager(
                beginForward: (systemName, tgid, _) =>
                {
                    begins.Add($"{systemName}|{tgid}");
                    return begins.Count == 1 ? 501u : 502u;
                },
                endForward: (_, _, _, _) => { },
                sendForwardAudio: (_, _, _, _) => { },
                getFallbackSourceId: (_, _) => 700,
                utcNow: () => now);

            manager.ApplyMemberships(CreateBasicMemberships());
            manager.HandleCallStart("Source", "100", 42, 99);
            manager.HandleCallEnd("Source", "100", 42);

            Assert.True(manager.IsPatchedTransmitStream("Target", "200", 501));
            now = now.AddMilliseconds(1999);
            Assert.True(manager.IsPatchedTransmitStream("Target", "200", 501));

            manager.HandleCallStart("Target", "200", 501, 99);
            Assert.Equal(new[] { "Target|200" }, begins);

            now = now.AddMilliseconds(1);
            Assert.False(manager.IsPatchedTransmitStream("Target", "200", 501));

            manager.HandleCallStart("Target", "200", 501, 99);
            Assert.Equal(new[] { "Target|200", "Source|100" }, begins);
        }

        private static Dictionary<string, List<PatchTalkgroupMember>> CreateBasicMemberships()
        {
            return new Dictionary<string, List<PatchTalkgroupMember>>
            {
                ["Group"] = new List<PatchTalkgroupMember>
                {
                    new PatchTalkgroupMember { SystemName = "Source", Tgid = "100" },
                    new PatchTalkgroupMember { SystemName = "Target", Tgid = "200" }
                }
            };
        }
    }
}
