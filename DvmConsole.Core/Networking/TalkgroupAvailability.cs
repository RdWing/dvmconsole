// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace DvmConsole.Core.Networking
{
    public enum TalkgroupMode
    {
        Dmr,
        P25,
    }

    public readonly record struct TalkgroupQuery
    {
        public TalkgroupQuery(uint talkgroupId, byte slot, TalkgroupMode mode)
        {
            TalkgroupId = talkgroupId;
            Slot = slot;
            Mode = mode;
        }

        public uint TalkgroupId { get; }
        public byte Slot { get; }
        public TalkgroupMode Mode { get; }
    }

    public readonly record struct TalkgroupRule
    {
        public TalkgroupRule(uint talkgroupId, byte announcedSlot, bool invalid)
        {
            TalkgroupId = talkgroupId;
            AnnouncedSlot = announcedSlot;
            Invalid = invalid;
        }

        public uint TalkgroupId { get; }
        public byte AnnouncedSlot { get; }
        public bool Invalid { get; }
    }

    public readonly record struct TalkgroupAvailability(
        bool IsAvailable,
        bool IsKnown,
        string Description);

    public interface IFneTalkgroupStatusProvider
    {
        TalkgroupAvailability QueryTalkgroupAvailability(TalkgroupQuery query);
    }

    public static class TalkgroupAvailabilityEvaluator
    {
        public static TalkgroupAvailability Evaluate(
            TalkgroupQuery query,
            IReadOnlyList<TalkgroupRule>? rules)
        {
            if (rules is null || rules.Count == 0)
            {
                return new TalkgroupAvailability(
                    IsAvailable: false,
                    IsKnown: false,
                    "no announced TG rules received yet");
            }

            var matches = rules
                .Where(rule => rule.TalkgroupId == query.TalkgroupId)
                .ToArray();
            if (matches.Length == 0)
            {
                return new TalkgroupAvailability(
                    IsAvailable: false,
                    IsKnown: true,
                    $"TG {query.TalkgroupId} not present in announced rules ({rules.Count} entries loaded)");
            }

            if (query.Mode == TalkgroupMode.P25)
            {
                return BuildResult(
                    matches.Any(rule => !rule.Invalid),
                    $"TG {query.TalkgroupId} entries: {Describe(matches)}");
            }

            byte desiredSlot = NormalizeQuerySlot(query.Slot);
            if (matches.Any(rule => !rule.Invalid && NormalizeAnnouncedSlot(rule.AnnouncedSlot) == desiredSlot))
            {
                return new TalkgroupAvailability(
                    IsAvailable: true,
                    IsKnown: true,
                    $"TG {query.TalkgroupId} available on DMR slot {desiredSlot + 1}");
            }

            // Some FNE rule pushes omit meaningful slot information. Match the
            // WPF behavior: when every matching entry is outside standard slot
            // values, any valid entry qualifies.
            bool hasStandardSlotInfo = matches.Any(rule => NormalizeAnnouncedSlot(rule.AnnouncedSlot) <= 1);
            bool available = !hasStandardSlotInfo && matches.Any(rule => !rule.Invalid);
            return new TalkgroupAvailability(
                available,
                IsKnown: true,
                available
                    ? $"TG {query.TalkgroupId} available without standard slot information"
                    : $"TG {query.TalkgroupId} requested on DMR slot {desiredSlot + 1}; entries: {Describe(matches)}");
        }

        private static TalkgroupAvailability BuildResult(bool available, string description)
            => new TalkgroupAvailability(available, IsKnown: true, description);

        private static string Describe(IEnumerable<TalkgroupRule> rules)
            => string.Join(
                ", ",
                rules.Select(rule =>
                    $"slot={NormalizeAnnouncedSlot(rule.AnnouncedSlot)} invalid={rule.Invalid}"));

        private static byte NormalizeQuerySlot(byte slot)
            => slot <= 1 ? (byte)0 : (byte)(slot - 1);

        private static byte NormalizeAnnouncedSlot(byte slot)
            => (byte)(slot & 0x03);
    }
}
