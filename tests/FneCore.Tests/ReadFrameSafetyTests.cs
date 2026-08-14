// SPDX-License-Identifier: AGPL-3.0-only
using fnecore;
using Xunit;

namespace FneCore.Tests
{
    public sealed class ReadFrameSafetyTests
    {
        private sealed class ReadFrameProbe : FneBase
        {
            public ReadFrameProbe()
                : base("ReadFrameProbe", 1)
            {
            }

            public byte[] Read(byte[] frame)
            {
                return ReadFrame(
                    new UdpFrame { Message = frame },
                    out _,
                    out _,
                    out _);
            }

            public byte[] Write(byte[] message)
            {
                return WriteFrame(
                    message,
                    1,
                    1,
                    FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25),
                    0,
                    1);
            }

            public override void Start()
            {
            }

            public override void Stop()
            {
            }
        }

        [Fact]
        public void ReadFrame_TruncatedPayload_ReturnsNullInsteadOfThrowing()
        {
            var probe = new ReadFrameProbe();
            var frame = probe.Write(new byte[8]);
            Array.Resize(ref frame, frame.Length - 4);

            byte[] decoded = null;
            var exception = Record.Exception(() => decoded = probe.Read(frame));

            Assert.Null(exception);
            Assert.Null(decoded);
        }
    }
}