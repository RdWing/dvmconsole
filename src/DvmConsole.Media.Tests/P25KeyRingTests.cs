using DvmConsole.Core.Configuration;
using DvmConsole.Media;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Media.Tests;

public sealed class P25KeyRingTests
{
    private const string SystemName = "System 1";
    private const string Aes256Key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    [Theory]
    [InlineData("aes", "0x50", P25Defines.P25_ALGO_AES)]
    [InlineData("des-ofb", "81", P25Defines.P25_ALGO_DES)]
    [InlineData("adp", "170", P25Defines.P25_ALGO_ARC4)]
    public void ResolvesConfiguredAlgorithmAndKeyId(string algorithm, string keyId, byte algorithmId)
    {
        var ring = new P25KeyRing(SystemName, new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = Aes256Key
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

        Assert.True(ring.CanResolve(SystemName, algorithm, keyId));
        Assert.True(P25KeyRing.TryParseAlgorithmId(algorithm, out byte parsedAlgorithm));
        Assert.Equal(algorithmId, parsedAlgorithm);
        Assert.True(P25KeyRing.TryParseKeyId(keyId, out ushort parsedKeyId));
        Assert.True(ring.TryResolve(SystemName, parsedAlgorithm, parsedKeyId, out ReadOnlyMemory<byte> key));
        Assert.NotEmpty(key.ToArray());
    }

    [Fact]
    public void RejectsUnsupportedLocalKeys()
    {
        Assert.Throws<FormatException>(() => new P25KeyRing(SystemName, new KeyContainer
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
            }));
    }

    [Fact]
    public void DoesNotResolveMissingKeys()
    {
        using var ring = new P25KeyRing();

        Assert.False(ring.CanResolve(SystemName, "unsupported", "0x50"));
        Assert.False(ring.CanResolve(SystemName, "aes", "0x51"));
    }

    [Theory]
    [InlineData(P25Defines.P25_ALGO_AES, "00112233445566778899AABBCCDDEEFF")]
    [InlineData(P25Defines.P25_ALGO_DES, "00112233445566")]
    [InlineData(P25Defines.P25_ALGO_ARC4, "00112233")]
    public void RejectsIncorrectKeyLengths(byte algorithmId, string key)
    {
        Assert.Throws<FormatException>(() => new P25KeyRing(SystemName, new KeyContainer
            {
                Keys =
                [
                    new KeyEntry
                    {
                        KeyId = 0x50,
                        AlgId = algorithmId,
                        Key = key
                    }
                ]
            }));
    }

    [Fact]
    public void IsolatesMatchingAlgorithmAndKeyIdsBySystem()
    {
        using var ring = new P25KeyRing();
        byte[] systemAKey = Convert.FromHexString(Aes256Key);
        byte[] systemBKey = Convert.FromHexString(
            "FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100");

        ring.AddOrReplaceFromFne("System A", P25Defines.P25_ALGO_AES, 0x50, systemAKey);
        ring.AddOrReplaceFromFne("System B", P25Defines.P25_ALGO_AES, 0x50, systemBKey);

        Assert.True(ring.TryResolve("system a", P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> resolvedA));
        Assert.True(ring.TryResolve("SYSTEM B", P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> resolvedB));
        Assert.Equal(systemAKey, resolvedA.ToArray());
        Assert.Equal(systemBKey, resolvedB.ToArray());
    }

    [Fact]
    public void PrefersFneKeyAndFallsBackToLocalKeyAfterDisconnect()
    {
        using var ring = new P25KeyRing("Local System", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = Aes256Key
                }
            ]
        });
        ring.AddOrReplaceFromFne(
            "Local System",
            P25Defines.P25_ALGO_AES,
            0x50,
            Convert.FromHexString("FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100"));

        Assert.True(ring.TryResolve(
            "Local System",
            P25Defines.P25_ALGO_AES,
            0x50,
            out ReadOnlyMemory<byte> fneKey));
        Assert.Equal(0xFF, fneKey.Span[0]);
        Assert.True(ring.TryGetSource(
            "Local System",
            P25Defines.P25_ALGO_AES,
            0x50,
            out P25KeyMaterialSource connectedSource));
        Assert.Equal(P25KeyMaterialSource.FneKmm, connectedSource);

        ring.ClearFneKeys("Local System");

        Assert.True(ring.CanResolve("Local System", "aes", "0x50"));
        Assert.True(ring.TryResolve(
            "Local System",
            P25Defines.P25_ALGO_AES,
            0x50,
            out ReadOnlyMemory<byte> localKey));
        Assert.Equal(0x00, localKey.Span[0]);
        Assert.True(ring.TryGetSource(
            "Local System",
            P25Defines.P25_ALGO_AES,
            0x50,
            out P25KeyMaterialSource source));
        Assert.Equal(P25KeyMaterialSource.LocalFile, source);
    }

    [Fact]
    public void AddsReplacesAndClonesRuntimeKeyMaterial()
    {
        using var ring = new P25KeyRing();
        byte[] initial = Convert.FromHexString(Aes256Key);

        ring.AddOrReplaceFromFne(SystemName, P25Defines.P25_ALGO_AES, 0x50, initial);
        initial[0] = 0xFF;

        Assert.True(ring.TryResolve(SystemName, P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> resolvedInitial));
        Assert.Equal(0x00, resolvedInitial.Span[0]);

        byte[] resolvedCopy = resolvedInitial.ToArray();
        resolvedCopy[0] = 0xEE;
        Assert.True(ring.TryResolve(SystemName, P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> unchanged));
        Assert.Equal(0x00, unchanged.Span[0]);

        ring.AddOrReplaceFromFne(
            SystemName,
            P25Defines.P25_ALGO_AES,
            0x50,
            Convert.FromHexString("FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100"));
        Assert.True(ring.TryResolve(SystemName, P25Defines.P25_ALGO_AES, 0x50, out ReadOnlyMemory<byte> replaced));
        Assert.Equal(0xFF, replaced.Span[0]);
    }
}
