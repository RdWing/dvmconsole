// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Media;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class SystemScopedLocalKeyTests
{
    [Fact]
    public void MatchingKeyIdentitiesRemainIsolatedAcrossFneSystems()
    {
        const string p25A = "00112233445566778899AABBCCDDEEFF";
        const string p25B = "FFEEDDCCBBAA99887766554433221100";
        const string dmrA = "0102030405";
        const string dmrB = "A1A2A3A4A5";
        const string nxdnA = "1234";
        const string nxdnB = "5678";
        var keys = new KeyContainer
        {
            Keys =
            [
                Key("System A", "p25", P25Defines.P25_ALGO_AES, p25A),
                Key("System B", "p25", P25Defines.P25_ALGO_AES, p25B),
                Key("System A", "dmr", DmrPrivacyAlgorithms.Arc4, dmrA),
                Key("System B", "dmr", DmrPrivacyAlgorithms.Arc4, dmrB),
                Key("System A", "nxdn", NxdnPrivacyAlgorithms.Ehr, nxdnA),
                Key("System B", "nxdn", NxdnPrivacyAlgorithms.Ehr, nxdnB)
            ]
        };
        using var p25 = new P25KeyRing();
        using var dmr = new DmrKeyRing();
        using var nxdn = new NxdnKeyRing();

        foreach (string system in new[] { "System A", "System B" })
        {
            p25.AddLocalKeys(system, keys);
            dmr.AddLocalKeys(system, keys);
            nxdn.AddLocalKeys(system, keys);
        }

        AssertResolved(p25, "System A", P25Defines.P25_ALGO_AES, p25A);
        AssertResolved(p25, "System B", P25Defines.P25_ALGO_AES, p25B);
        AssertResolved(dmr, "System A", DmrPrivacyAlgorithms.Arc4, dmrA);
        AssertResolved(dmr, "System B", DmrPrivacyAlgorithms.Arc4, dmrB);
        AssertResolved(nxdn, "System A", NxdnPrivacyAlgorithms.Ehr, nxdnA);
        AssertResolved(nxdn, "System B", NxdnPrivacyAlgorithms.Ehr, nxdnB);
    }

    [Fact]
    public void LegacyUnscopedKeysRemainAvailableToEveryFneSystem()
    {
        const string material = "0102030405";
        var keys = new KeyContainer
        {
            Keys = [Key(string.Empty, "dmr", DmrPrivacyAlgorithms.Arc4, material)]
        };
        using var ring = new DmrKeyRing();

        ring.AddLocalKeys("System A", keys);
        ring.AddLocalKeys("System B", keys);

        AssertResolved(ring, "System A", DmrPrivacyAlgorithms.Arc4, material);
        AssertResolved(ring, "System B", DmrPrivacyAlgorithms.Arc4, material);
    }

    private static KeyEntry Key(string system, string protocol, int algorithm, string material)
        => new()
        {
            System = system,
            Protocol = protocol,
            KeyId = 1,
            AlgId = algorithm,
            Key = material
        };

    private static void AssertResolved(
        P25KeyRing ring,
        string system,
        byte algorithm,
        string expected)
    {
        Assert.True(ring.TryResolve(system, algorithm, 1, out ReadOnlyMemory<byte> material));
        Assert.Equal(Convert.FromHexString(expected), material.Span[..(expected.Length / 2)].ToArray());
    }

    private static void AssertResolved(
        DmrKeyRing ring,
        string system,
        byte algorithm,
        string expected)
    {
        Assert.True(ring.TryResolve(system, algorithm, 1, out ReadOnlyMemory<byte> material));
        Assert.Equal(Convert.FromHexString(expected), material.ToArray());
    }

    private static void AssertResolved(
        NxdnKeyRing ring,
        string system,
        byte algorithm,
        string expected)
    {
        Assert.True(ring.TryResolve(system, algorithm, 1, out ReadOnlyMemory<byte> material));
        Assert.Equal(Convert.FromHexString(expected), material.ToArray());
    }
}
