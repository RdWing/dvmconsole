// SPDX-License-Identifier: AGPL-3.0-only
/**
* Production extraction ownership gate: Codeplug and RadioAlias must live in
* the portable DvmConsole.Core assembly (not the WPF app or the test
* assembly), and the P25 algorithm id constants used by the codeplug schema
* must be owned by DvmConsole.Core as literal, verified values.
*/
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Asserts the first production extraction boundary: Codeplug and
    /// RadioAlias compile into the DvmConsole.Core assembly so the WPF
    /// console and headless tooling share one portable definition.
    /// </summary>
    public class CodeplugOwnershipTests
    {
        /// <summary>
        /// Codeplug must be compiled into the DvmConsole.Core assembly. If it
        /// regresses to a linked compile in the WPF or test project, the
        /// extraction boundary is broken.
        /// </summary>
        [Fact]
        public void Codeplug_LivesInDvmConsoleCoreAssembly()
        {
            Assert.Equal("DvmConsole.Core", typeof(Codeplug).Assembly.GetName().Name);
        }

        /// <summary>
        /// Codeplug and RadioAlias must share the same assembly: RadioAlias is
        /// part of the codeplug configuration surface and must not drift into
        /// a separate assembly from Codeplug.
        /// </summary>
        [Fact]
        public void CodeplugAndRadioAlias_ShareSameAssembly()
        {
            Assert.Same(typeof(Codeplug).Assembly, typeof(RadioAlias).Assembly);
        }

        /// <summary>
        /// The P25 algorithm id constants are the codeplug's on-disk schema
        /// values and must equal the verified fnecore P25Defines literals
        /// (0x80 unencrypt, 0x81 DES, 0x84 AES, 0xAA ARC4).
        /// </summary>
        [Fact]
        public void P25AlgoIds_MatchVerifiedFnecoreLiterals()
        {
            Assert.Equal(0x80, P25AlgoIds.P25_ALGO_UNENCRYPT);
            Assert.Equal(0x81, P25AlgoIds.P25_ALGO_DES);
            Assert.Equal(0x84, P25AlgoIds.P25_ALGO_AES);
            Assert.Equal(0xAA, P25AlgoIds.P25_ALGO_ARC4);
        }
    }
}
