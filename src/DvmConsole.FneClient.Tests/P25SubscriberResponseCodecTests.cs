// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.FneClient;
using fnecore;
using fnecore.EDAC;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class P25SubscriberResponseCodecTests
{
    // Hand-authored payload fields, independent of fnecore's TSBK field decoder.
    [Theory]
    [InlineData("A0009F0000037A003039", P25SubscriberCommand.CallAlert)]
    [InlineData("A400008000303900037A", P25SubscriberCommand.RadioCheck)]
    [InlineData("A40000FF00303900037A", P25SubscriberCommand.Inhibit)]
    [InlineData("A40000FE00303900037A", P25SubscriberCommand.Uninhibit)]
    public void DecodesAcknowledgementsAndRejectsEveryTruncation(string prefix, P25SubscriberCommand command)
    {
        byte[] frame = Frame(prefix);
        Assert.True(P25SubscriberResponseCodec.TryDecode(frame, out var response));
        Assert.Equal(new P25SubscriberResponse(command, 12345, 890), response);
        for (int length = 0; length < 69; length++)
            Assert.False(P25SubscriberResponseCodec.TryDecode(frame.AsSpan(0, length), out _));
        frame[23] = 68;
        Assert.False(P25SubscriberResponseCodec.TryDecode(frame, out _));
    }

    [Theory]
    [InlineData("A0001F0000037A003039")] // No target address supplied.
    [InlineData("A000DF0000037A003039")] // Extended network addressing.
    [InlineData("A0009E0000037A003039")] // Different acknowledged service.
    [InlineData("A400000000303900037A")] // A request is not an acknowledgement.
    [InlineData("A490008000303900037A")] // Manufacturer-specific response.
    [InlineData("E400008000303900037A")] // Protected block.
    [InlineData("A400008000000000037A")] // Invalid source RID.
    public void DoesNotMisidentifyOtherSignallingAsAcknowledgement(string prefix)
        => Assert.False(P25SubscriberResponseCodec.TryDecode(Frame(prefix), out _));

    [Fact]
    public void RejectsBadCrcAndVoiceFrames()
    {
        Assert.False(P25SubscriberResponseCodec.TryDecode(Frame("A400008000303900037A", corruptCrc: true), out _));
        byte[] frame = Frame("A400008000303900037A");
        frame[22] = 5;
        Assert.False(P25SubscriberResponseCodec.TryDecode(frame, out _));
        frame[22] = 7;
        frame[0] = 0;
        Assert.False(P25SubscriberResponseCodec.TryDecode(frame, out _));
    }

    private static byte[] Frame(string prefix, bool corruptCrc = false)
    {
        byte[] block = new byte[12];
        Convert.FromHexString(prefix).CopyTo(block, 0);
        CRC.AddCCITT162(ref block, 12);
        if (corruptCrc) block[11] ^= 1;
        return P25SubscriberFrameEncoder.Encode(new(P25SubscriberCommand.RadioCheck, 12345, 890, 0x24, block),
            new RemoteCallData { SrcId = 12345, DstId = 890, LCO = 0x24 });
    }
}
