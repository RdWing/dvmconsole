// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class RadioMediaIngressFrameTests
{
    [Fact]
    public void PortableFrameRetainsTransportTimingAndPayloadIdentity()
    {
        var frame = new TimedFrame();
        RadioMediaIngressFrame ingress = RadioMediaIngressFrame.FromFrame(frame);
        Assert.Same(frame, ingress.Traffic);
        Assert.Same(frame.Payload, ingress.Traffic.Payload);
        Assert.Equal(500, ingress.BoundaryTimestamp);
        Assert.Equal(100, ingress.TransportIngressTimestamp);
        Assert.Null(ingress.Encryption);
    }

    [Fact]
    public void ExplicitBoundaryOverridesFrameBoundaryAndRetainsValidation()
    {
        var frame = new TimedFrame();
        Assert.Equal(700, RadioMediaIngressFrame.FromFrame(frame, 700).BoundaryTimestamp);
        Assert.Throws<ArgumentOutOfRangeException>(() => RadioMediaIngressFrame.FromFrame(frame, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RadioMediaIngressFrame.FromFrame(frame, 50));
    }

    [Fact]
    public void UntimedFramesUseHostBoundaryWithoutInventingTransportTiming()
    {
        RadioMediaIngressFrame ingress = RadioMediaIngressFrame.FromFrame(new Frame());
        Assert.True(ingress.BoundaryTimestamp > 0);
        Assert.Equal(0, ingress.TransportIngressTimestamp);
    }

    private class Frame : IRadioMediaFrame
    {
        public RadioMediaProtocol Protocol => RadioMediaProtocol.Analog;
        public uint PeerId => 1;
        public uint SourceId => 2;
        public uint DestinationId => 3;
        public byte? Slot => null;
        public string CallType => "GROUP";
        public string FrameType => "VOICE";
        public string Subtype => "VOICE";
        public ushort PacketSequence => 1;
        public uint StreamId => 42;
        public byte[] Payload { get; } = [1, 2, 3];
    }

    private sealed class TimedFrame : Frame, IRadioFrameIngressTiming
    {
        public long BoundaryTimestamp => 500;
        public long TransportIngressTimestamp => 100;
    }
}
