// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gates for the DvmConsole.Platform.Audio.Mac first slice:
* MacAudioDeviceKey (pure key building, default-key detection, case-insensitive
* matching and AudioDeviceId conversion) and MacAudioBufferPolicy (bounded
* FIFO byte buffer with backlog shedding). These facts are written entirely
* against the approved CoreAudio design. Nothing in this file may reference
* Windows sentinel keys, and the
* exact capacity math (8 kHz * 2 bytes * 1 ch * 10 s == 160000 bytes) is
* locked so the implementation cannot drift from the codec framing.
*/
#nullable enable
using System;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Contract gate for the pure-managed <c>MacAudioDeviceKey</c> factory:
    /// normalized key building, default-key detection and case-insensitive
    /// matching that never reuses Windows sentinel markers.
    /// </summary>
    public sealed class MacAudioDeviceKeyContractTests
    {
        /// <summary>
        /// Keys are namespaced by direction and start with a mac prefix; the
        /// direction must be recoverable from the first field.
        /// </summary>
        [Theory]
        [InlineData(AudioDeviceDirection.Input, "mac|input|")]
        [InlineData(AudioDeviceDirection.Output, "mac|output|")]
        public void BuildKey_Prefix_MatchesDirection(
            AudioDeviceDirection direction, string expectedPrefix)
        {
            var key = MacAudioDeviceKey.BuildKey(direction, "uid-1", "Device", 1);

            Assert.StartsWith(expectedPrefix, key);
        }

        /// <summary>
        /// Building the same device twice must yield the identical key, and
        /// the key must carry the uid, channel count and name so it stays
        /// debuggable and self-describing.
        /// </summary>
        [Fact]
        public void BuildKey_IsStable_AndCarriesUidNameAndChannelFields()
        {
            const string uid = "AppleHDAEngineInput:1B,0,1,0:1";
            const string name = "Built-In Microphone";

            var first = MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, uid, name, 1);
            var second = MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, uid, name, 1);

            Assert.Equal(first, second);
            Assert.Contains(uid, first);
            Assert.Contains(name, first);
            Assert.EndsWith("|1", first);
        }

        /// <summary>
        /// The Mac key format must not reuse the Windows default-device and
        /// master-output sentinel markers: sentinels are platform-specific
        /// and a Mac key is never one of them.
        /// </summary>
        [Fact]
        public void BuildKey_DoesNotReuseWindowsSentinelMarkers()
        {
            var key = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Output, "uid-9", "Studio Speakers", 2);

            Assert.DoesNotContain("windows-default", key);
            Assert.DoesNotContain("inherit-master-output", key);
        }

        /// <summary>
        /// The same device reported with different casing (CoreAudio UIDs and
        /// display names are not case-stable across versions) must produce
        /// keys that compare equal through <see cref="MacAudioDeviceKey.Matches"/>.
        /// </summary>
        [Fact]
        public void BuildKey_CaseVariants_MatchCaseInsensitively()
        {
            var upper = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Input, "UID-ABC-123", "Built-In Microphone", 1);
            var lower = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Input, "uid-abc-123", "built-in microphone", 1);

            Assert.True(MacAudioDeviceKey.Matches(upper, lower));
            Assert.True(MacAudioDeviceKey.Matches(lower, upper));
        }

        /// <summary>
        /// Null, empty and whitespace keys all mean "the OS default device".
        /// </summary>
        [Fact]
        public void IsDefaultKey_NullEmptyWhitespace_IsDefault()
        {
            Assert.True(MacAudioDeviceKey.IsDefaultKey(null));
            Assert.True(MacAudioDeviceKey.IsDefaultKey(string.Empty));
            Assert.True(MacAudioDeviceKey.IsDefaultKey(" \t "));
        }

        /// <summary>
        /// A real Mac key is never the default marker, and a foreign Windows
        /// sentinel must not be honored as a default on macOS.
        /// </summary>
        [Fact]
        public void IsDefaultKey_MacKeyAndForeignSentinel_AreNotDefault()
        {
            var macKey = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Input, "uid-4", "Mic", 1);

            Assert.False(MacAudioDeviceKey.IsDefaultKey(macKey));
            Assert.False(MacAudioDeviceKey.IsDefaultKey("windows-default"));
        }

        /// <summary>
        /// Null and empty (and whitespace) represent the same default device:
        /// they only match each other, never a real key.
        /// </summary>
        [Fact]
        public void Matches_NullAndEmpty_OnlyMatchEachOtherAsDefault()
        {
            Assert.True(MacAudioDeviceKey.Matches(null, null));
            Assert.True(MacAudioDeviceKey.Matches(null, string.Empty));
            Assert.True(MacAudioDeviceKey.Matches(string.Empty, null));
            Assert.True(MacAudioDeviceKey.Matches("   ", null));

            var macKey = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Output, "uid-5", "Speakers", 2);
            Assert.False(MacAudioDeviceKey.Matches(null, macKey));
            Assert.False(MacAudioDeviceKey.Matches(string.Empty, macKey));
        }

        /// <summary>
        /// Keys built from different device UIDs never match, even when the
        /// display names and channel counts are identical.
        /// </summary>
        [Fact]
        public void Matches_DifferentUids_DoNotMatch()
        {
            var first = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Input, "uid-1", "Mic A", 1);
            var second = MacAudioDeviceKey.BuildKey(
                AudioDeviceDirection.Input, "uid-2", "Mic A", 1);

            Assert.False(MacAudioDeviceKey.Matches(first, second));
            Assert.False(MacAudioDeviceKey.Matches(second, first));
        }

        /// <summary>
        /// A concrete device converts to the non-default id produced by
        /// <see cref="AudioDeviceId.FromKey"/> of its own key, and that id is
        /// never the empty default marker.
        /// </summary>
        [Fact]
        public void ToDeviceId_NonDefault_EqualsFromKeyOfBuildKey_AndIsNotEmpty()
        {
            const string uid = "uid-7";
            const string name = "Speakers";

            var expected = AudioDeviceId.FromKey(
                MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Output, uid, name, 2));
            var actual = MacAudioDeviceKey.ToDeviceId(
                AudioDeviceDirection.Output, uid, name, 2);

            Assert.Equal(expected, actual);
            Assert.False(actual.IsEmpty);
            Assert.False(actual.IsDefault);
        }

        /// <summary>
        /// The default-device marker is the shared
        /// <see cref="AudioDeviceId.Default"/> empty id, never a synthetic key.
        /// </summary>
        [Fact]
        public void ToDefaultDeviceId_EqualsDefaultMarker()
        {
            var id = MacAudioDeviceKey.ToDefaultDeviceId();

            Assert.Equal(AudioDeviceId.Default, id);
            Assert.True(id.IsEmpty);
            Assert.True(id.IsDefault);
        }

        /// <summary>
        /// A null or whitespace uid or name is a programming error: a Mac key
        /// always carries a real device identity.
        /// </summary>
        [Fact]
        public void BuildKey_WhitespaceUidOrName_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(
                () => MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, null!, "Mic", 1));
            Assert.Throws<ArgumentException>(
                () => MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, "   ", "Mic", 1));
            Assert.Throws<ArgumentException>(
                () => MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, "uid-1", null!, 1));
            Assert.Throws<ArgumentException>(
                () => MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, "uid-1", " \t", 1));
        }

        /// <summary>
        /// A device always has at least one channel: zero and negative channel
        /// counts are rejected as out of range.
        /// </summary>
        [Fact]
        public void BuildKey_NonPositiveChannels_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, "uid-1", "Mic", 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MacAudioDeviceKey.BuildKey(AudioDeviceDirection.Input, "uid-1", "Mic", -2));
        }
    }

    /// <summary>
    /// Contract gate for the bounded FIFO <c>MacAudioBufferPolicy</c>: capacity
    /// is derived from the PCM byte rate and max duration, writes are atomic,
    /// reads preserve order, and backlog shedding drops oldest bytes first.
    /// </summary>
    public sealed class MacAudioBufferPolicyContractTests
    {
        private const int BlockBytes = AudioPcm.BlockBytes;

        /// <summary>
        /// Capacity is bounded from the format byte rate times the max
        /// duration: for the locked console codec (8 kHz, 16-bit, mono) and
        /// ten seconds that is exactly 160000 bytes.
        /// </summary>
        [Fact]
        public void Constructor_ConsoleTenSeconds_CapacityIsExactly160000()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));

            Assert.Equal(160000, policy.CapacityBytes);
            Assert.Equal(0, policy.BufferedBytes);
            Assert.Equal(TimeSpan.Zero, policy.BufferedDuration);
        }

        /// <summary>
        /// A zero or negative max duration would produce a degenerate buffer
        /// and is a programming error.
        /// </summary>
        [Fact]
        public void Constructor_NonPositiveMaxDuration_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MacAudioBufferPolicy(AudioPcm.Console, TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MacAudioBufferPolicy(AudioPcm.Console, TimeSpan.FromSeconds(-1)));
        }

        /// <summary>
        /// A format with a non-positive byte rate (zero or negative sample
        /// rate, zero bits per sample, or zero channels) cannot bound a
        /// duration and is rejected up front.
        /// </summary>
        [Fact]
        public void Constructor_NonPositiveBytesPerSecond_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MacAudioBufferPolicy(new PcmFormat(8000, 0, 1), TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MacAudioBufferPolicy(new PcmFormat(0, 16, 1), TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MacAudioBufferPolicy(new PcmFormat(-8000, 16, 1), TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new MacAudioBufferPolicy(new PcmFormat(8000, 16, 0), TimeSpan.FromSeconds(1)));
        }

        /// <summary>
        /// Writes within capacity are accepted, buffered byte count tracks the
        /// payload, and buffered duration follows the byte rate (1600 bytes of
        /// console codec is 100 ms of audio).
        /// </summary>
        [Fact]
        public void TryWrite_WithinCapacity_AppendsAndTracksState()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));
            var payload = Pattern(BlockBytes, 1);

            Assert.True(policy.TryWrite(payload));

            Assert.Equal(BlockBytes, policy.BufferedBytes);
            Assert.True(policy.BufferedDuration > TimeSpan.Zero);
            Assert.True(policy.BufferedDuration <= TimeSpan.FromMilliseconds(100));
        }

        /// <summary>
        /// A write that does not fit is rejected as a whole: it returns false,
        /// never exceeds capacity and never appends a partial payload.
        /// </summary>
        [Fact]
        public void TryWrite_Overflow_RejectsWholeWrite_WithoutPartialAppend()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));
            var pattern = Pattern(160000, 3);
            for (var offset = 0; offset < pattern.Length; offset += BlockBytes)
            {
                Assert.True(policy.TryWrite(pattern.AsMemory(offset, BlockBytes)));
            }

            Assert.Equal(160000, policy.BufferedBytes);

            Assert.False(policy.TryWrite(Pattern(BlockBytes, 4)));
            Assert.Equal(160000, policy.BufferedBytes);

            var contents = new byte[policy.BufferedBytes];
            Assert.Equal(contents.Length, policy.Read(contents));
            Assert.True(pattern.AsSpan().SequenceEqual(contents));
        }

        /// <summary>
        /// Reading is FIFO: an empty buffer reads zero, and data comes back in
        /// exact write order until the buffer is drained again.
        /// </summary>
        [Fact]
        public void Read_EmptyBuffer_ReturnsZero_ThenFifoOrderAfterWrites()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));
            var first = Pattern(BlockBytes, 1);
            var second = Pattern(BlockBytes, 2);
            var probe = new byte[BlockBytes];

            Assert.Equal(0, policy.Read(probe));

            Assert.True(policy.TryWrite(first));
            Assert.True(policy.TryWrite(second));
            Assert.Equal(2 * BlockBytes, policy.BufferedBytes);

            var firstRead = new byte[BlockBytes];
            Assert.Equal(BlockBytes, policy.Read(firstRead));
            Assert.True(first.AsSpan().SequenceEqual(firstRead));

            var secondRead = new byte[2 * BlockBytes];
            Assert.Equal(BlockBytes, policy.Read(secondRead));
            Assert.True(second.AsSpan().SequenceEqual(secondRead.AsSpan(0, BlockBytes)));

            Assert.Equal(0, policy.Read(probe));
            Assert.Equal(0, policy.BufferedBytes);
        }

        /// <summary>
        /// Writes and reads that cross the physical ring boundary preserve
        /// FIFO order after the head has advanced away from zero.
        /// </summary>
        [Fact]
        public void RingWrapAfterPartialRead_PreservesFifoOrder()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(1));
            var first = Pattern(15000, 10);
            var second = Pattern(4000, 11);

            Assert.True(policy.TryWrite(first));

            var discarded = new byte[14000];
            Assert.Equal(discarded.Length, policy.Read(discarded));
            Assert.True(policy.TryWrite(second));

            var expected = new byte[1000 + second.Length];
            Array.Copy(first, 14000, expected, 0, 1000);
            Array.Copy(second, 0, expected, 1000, second.Length);
            var actual = new byte[expected.Length];
            Assert.Equal(actual.Length, policy.Read(actual));
            Assert.True(expected.AsSpan().SequenceEqual(actual));
        }

        /// <summary>
        /// Shedding above the threshold removes the oldest bytes until the
        /// buffered duration fits, returns true because bytes were removed,
        /// and preserves the newest bytes (the tail of the stream).
        /// </summary>
        [Fact]
        public void ShedBacklogIfOver_RemovesOldestUntilWithinThreshold_PreservingNewest()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));
            var pattern = Pattern(160000, 5);
            for (var offset = 0; offset < pattern.Length; offset += BlockBytes)
            {
                Assert.True(policy.TryWrite(pattern.AsMemory(offset, BlockBytes)));
            }

            var shedDuration = TimeSpan.FromSeconds(5);
            Assert.True(policy.ShedBacklogIfOver(shedDuration));

            Assert.True(policy.BufferedBytes > 0);
            Assert.True(policy.BufferedBytes <= 80000);
            Assert.True(policy.BufferedDuration <= shedDuration);

            var remaining = new byte[policy.BufferedBytes];
            Assert.Equal(remaining.Length, policy.Read(remaining));
            Assert.True(pattern.AsSpan(pattern.Length - remaining.Length).SequenceEqual(remaining));
        }

        /// <summary>
        /// Shedding at or below the threshold is a no-op: it returns false and
        /// leaves every buffered byte untouched.
        /// </summary>
        [Fact]
        public void ShedBacklogIfOver_BelowThreshold_ReturnsFalse_AndLeavesBufferUntouched()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));
            var payload = Pattern(BlockBytes, 6);
            Assert.True(policy.TryWrite(payload));

            Assert.False(policy.ShedBacklogIfOver(TimeSpan.FromSeconds(10)));
            Assert.Equal(BlockBytes, policy.BufferedBytes);

            var contents = new byte[policy.BufferedBytes];
            Assert.Equal(contents.Length, policy.Read(contents));
            Assert.True(payload.AsSpan().SequenceEqual(contents));
        }

        /// <summary>
        /// Clear empties the buffer completely, is safe to repeat, and the
        /// buffer remains usable afterwards.
        /// </summary>
        [Fact]
        public void Clear_EmptiesBuffer_AndIsSafeToRepeat()
        {
            var policy = new MacAudioBufferPolicy(
                AudioPcm.Console, TimeSpan.FromSeconds(10));
            Assert.True(policy.TryWrite(Pattern(BlockBytes, 7)));
            Assert.True(policy.TryWrite(Pattern(BlockBytes, 8)));

            policy.Clear();

            Assert.Equal(0, policy.BufferedBytes);
            Assert.Equal(TimeSpan.Zero, policy.BufferedDuration);
            var probe = new byte[BlockBytes];
            Assert.Equal(0, policy.Read(probe));

            policy.Clear();

            Assert.True(policy.TryWrite(Pattern(BlockBytes, 9)));
            Assert.Equal(BlockBytes, policy.BufferedBytes);
        }

        /// <summary>
        /// Deterministic byte pattern for a payload of the given length: every
        /// byte is a pure function of its index, so FIFO order, tail-preserving
        /// shedding and partial-append failures are all verifiable by exact
        /// comparison.
        /// </summary>
        private static byte[] Pattern(int length, int salt)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                bytes[i] = (byte)((i * 7 + salt) % 251);
            }

            return bytes;
        }
    }
}
