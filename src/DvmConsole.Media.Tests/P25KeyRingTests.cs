using DvmConsole.Core.Configuration;
using DvmConsole.Media;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25KeyRingTests
{
    [Theory]
    [InlineData("aes", "0x50", P25Defines.P25_ALGO_AES)]
    [InlineData("des-ofb", "81", P25Defines.P25_ALGO_DES)]
    [InlineData("adp", "170", P25Defines.P25_ALGO_ARC4)]
    public void ResolvesConfiguredAlgorithmAndKeyId(string algorithm, string keyId, byte algorithmId)
    {
        var ring = new P25KeyRing(new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                },
                new KeyEntry
                {
                    KeyId = 81,
                    AlgId = P25Defines.P25_ALGO_DES,
                    Key = "0011223344556677"
                },
                new KeyEntry
                {
                    KeyId = 170,
                    AlgId = P25Defines.P25_ALGO_ARC4,
                    Key = "0011223344"
                }
            ]
        });

        Assert.True(ring.CanResolve(algorithm, keyId));
        Assert.True(P25KeyRing.TryParseAlgorithmId(algorithm, out byte parsedAlgorithm));
        Assert.Equal(algorithmId, parsedAlgorithm);
        Assert.True(P25KeyRing.TryParseKeyId(keyId, out ushort parsedKeyId));
        Assert.True(ring.TryResolve(parsedAlgorithm, parsedKeyId, out ReadOnlyMemory<byte> key));
        Assert.NotEmpty(key.ToArray());
    }

    [Fact]
    public void DoesNotResolveUnsupportedOrMissingKeys()
    {
        var ring = new P25KeyRing(new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = 0x12,
                    Key = "00112233"
                }
            ]
        });

        Assert.False(ring.CanResolve("unsupported", "0x50"));
        Assert.False(ring.CanResolve("aes", "0x51"));
    }

    [Fact]
    public void AddsReplacesAndClonesRuntimeKeyMaterial()
    {
        var ring = new P25KeyRing(new KeyContainer());
        byte[] initial = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

        ring.AddOrReplace(P25Defines.P25_ALGO_AES, 0x50, initial);
        initial[0] = 0xFF;

        Assert.True(ring.TryResolve(P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> resolvedInitial));
        Assert.Equal(0x00, resolvedInitial.Span[0]);

        byte[] resolvedCopy = resolvedInitial.ToArray();
        resolvedCopy[0] = 0xEE;
        Assert.True(ring.TryResolve(P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> unchanged));
        Assert.Equal(0x00, unchanged.Span[0]);

        ring.AddOrReplace(P25Defines.P25_ALGO_AES, 0x50, Convert.FromHexString("FFEEDDCCBBAA99887766554433221100"));
        Assert.True(ring.TryResolve(P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> replaced));
        Assert.Equal(0xFF, replaced.Span[0]);
    }
}
