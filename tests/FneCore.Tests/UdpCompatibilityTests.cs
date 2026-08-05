// SPDX-License-Identifier: AGPL-3.0-only
/**
* Parent-owned headless compatibility baseline for the pinned fnecore
* submodule: UDP transport surface. Tests only construct and connect
* receivers against loopback; no datagrams are sent or received, so the
* baseline never depends on a listener or external networking.
*/
using System.Net;
using fnecore;
using Xunit;

namespace FneCore.Tests
{
    /// <summary>
    /// UdpReceiver endpoint plumbing and the UdpFrame data shape.
    /// </summary>
    public class UdpCompatibilityTests
    {
        /// <summary>
        /// Connecting by host string must parse a literal IPv4 address and
        /// expose it through EndPoint.
        /// </summary>
        [Fact]
        public void UdpReceiver_ConnectHostName_SetsEndpoint()
        {
            var receiver = new UdpReceiver();
            receiver.Connect("127.0.0.1", 49152);

            Assert.NotNull(receiver.EndPoint);
            Assert.Equal(IPAddress.Parse("127.0.0.1"), receiver.EndPoint.Address);
            Assert.Equal(49152, receiver.EndPoint.Port);
        }

        /// <summary>
        /// Connecting by IPEndPoint must expose the same endpoint.
        /// </summary>
        [Fact]
        public void UdpReceiver_ConnectIPEndPoint_SetsEndpoint()
        {
            var receiver = new UdpReceiver();
            receiver.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 49153));

            Assert.NotNull(receiver.EndPoint);
            Assert.Equal("127.0.0.1", receiver.EndPoint.Address.ToString());
            Assert.Equal(49153, receiver.EndPoint.Port);
        }

        /// <summary>
        /// UdpFrame is a plain struct: default instance has null fields, and
        /// object-initializer assignment must stick.
        /// </summary>
        [Fact]
        public void UdpFrame_FieldsDefaultAndSettable()
        {
            UdpFrame empty = default;
            Assert.Null(empty.Endpoint);
            Assert.Null(empty.Message);

            var frame = new UdpFrame
            {
                Endpoint = new IPEndPoint(IPAddress.Loopback, 49154),
                Message = new byte[] { 0x01, 0x02, 0x03 }
            };

            Assert.Equal(IPAddress.Loopback, frame.Endpoint.Address);
            Assert.Equal(49154, frame.Endpoint.Port);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, frame.Message);
        }

        /// <summary>
        /// Preshared-key plumbing must accept and clear a key without any
        /// socket activity.
        /// </summary>
        [Fact]
        public void UdpReceiver_SetPresharedKey_TogglesWithoutThrowing()
        {
            var receiver = new UdpReceiver();

            receiver.SetPresharedKey(new byte[32]);
            receiver.SetPresharedKey(null);
            receiver.SetPresharedKey(new byte[16]);
        }
    }
}
