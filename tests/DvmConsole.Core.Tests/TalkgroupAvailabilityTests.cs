// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using DvmConsole.Core.Networking;
using Xunit;

namespace DvmConsole.Core.Tests
{
    public sealed class TalkgroupAvailabilityTests
    {
        [Fact]
        public void EmptyAnnouncementsAreUnavailableUntilRulesArrive()
        {
            var query = new TalkgroupQuery(31001, slot: 1, TalkgroupMode.Dmr);

            var result = TalkgroupAvailabilityEvaluator.Evaluate(
                query,
                Array.Empty<TalkgroupRule>());

            Assert.False(result.IsAvailable);
            Assert.False(result.IsKnown);
            Assert.Contains("no announced", result.Description, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DmrQueryMatchesTalkgroupAndOneBasedSlot()
        {
            var query = new TalkgroupQuery(31001, slot: 2, TalkgroupMode.Dmr);

            var result = TalkgroupAvailabilityEvaluator.Evaluate(
                query,
                new[] { new TalkgroupRule(31001, announcedSlot: 1, invalid: false) });

            Assert.True(result.IsAvailable);
            Assert.True(result.IsKnown);
        }

        [Fact]
        public void DmrWrongSlotIsUnavailable()
        {
            var query = new TalkgroupQuery(31001, slot: 1, TalkgroupMode.Dmr);

            var result = TalkgroupAvailabilityEvaluator.Evaluate(
                query,
                new[] { new TalkgroupRule(31001, announcedSlot: 1, invalid: false) });

            Assert.False(result.IsAvailable);
            Assert.True(result.IsKnown);
            Assert.Contains("slot", result.Description, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void P25QueryUsesAnyValidTalkgroupEntry()
        {
            var query = new TalkgroupQuery(31001, slot: 2, TalkgroupMode.P25);

            var result = TalkgroupAvailabilityEvaluator.Evaluate(
                query,
                new[] { new TalkgroupRule(31001, announcedSlot: 0, invalid: false) });

            Assert.True(result.IsAvailable);
        }

        [Fact]
        public void InvalidMatchingEntriesDoNotQualify()
        {
            var query = new TalkgroupQuery(31001, slot: 1, TalkgroupMode.Dmr);

            var result = TalkgroupAvailabilityEvaluator.Evaluate(
                query,
                new[] { new TalkgroupRule(31001, announcedSlot: 0, invalid: true) });

            Assert.False(result.IsAvailable);
            Assert.True(result.IsKnown);
        }
    }
}
