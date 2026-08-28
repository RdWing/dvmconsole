using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingPolicyTests
{
    [Fact]
    public void PathPolicyUsesCanonicalEncryptionAndSanitizesSystemName()
    {
        string root = CreateRoot();
        try
        {
            RecordingFinalizationDescriptor descriptor = CreateDescriptor(root) with
            {
                SystemName = "SKY/NET",
                IsSecure = true,
                EncryptionAlgorithmId = DmrPrivacyAlgorithms.Arc4,
                EncryptionKeyId = 3,
                IsEncryptionKnown = true
            };

            string path = new RecordingPathPolicy().CreatePath(descriptor);

            Assert.StartsWith(Path.GetFullPath(root), path, StringComparison.Ordinal);
            Assert.Contains("SKY_NET", path, StringComparison.Ordinal);
            Assert.Contains("SECURE_RC4", Path.GetFileName(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MetadataFactoryWritesOneConsistentEncryptionSnapshot()
    {
        string root = CreateRoot();
        try
        {
            RecordingFinalizationDescriptor descriptor = CreateDescriptor(root) with
            {
                IsSecure = true,
                EncryptionAlgorithmId = DmrPrivacyAlgorithms.DesOfb,
                EncryptionKeyId = 0x50,
                IsEncryptionKnown = true
            };
            var trim = new PcmWavTrimResult(
                OriginalSamples: 8_000,
                OutputSamples: 7_200,
                TrimLeadMs: 50,
                TrimTailMs: 50,
                PeakAmplitude: 10_000,
                ActiveSampleCount: 6_000);

            CallRecordingMetadata metadata = new RecordingMetadataFactory().Create(
                descriptor,
                trim,
                Path.Combine(root, "call.opus"));

            Assert.Equal(CallRecordingEncryptionState.Secure, metadata.EncryptionState);
            Assert.True(metadata.IsEncrypted);
            Assert.Equal(DmrPrivacyAlgorithms.DesOfb, metadata.EncryptionAlgorithmId);
            Assert.Equal("DES-OFB", metadata.EncryptionAlgorithm);
            Assert.Equal((ushort)0x50, metadata.EncryptionKeyIdValue);
            Assert.Equal("0x50", metadata.EncryptionKeyId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RecordingFinalizationDescriptor CreateDescriptor(string root)
    {
        Guid jobId = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
        return new RecordingFinalizationDescriptor(
            jobId,
            start,
            root,
            Path.Combine(root, ".active", $"{jobId:N}.wav"),
            string.Empty,
            8_000,
            1,
            16,
            FneTrafficProtocol.Dmr,
            "DMR",
            "RX",
            "InboundRadio",
            start,
            start.AddSeconds(1),
            "SKYNET",
            "Dispatch",
            3_100,
            1_001,
            "Unit 1001",
            51,
            [51],
            false,
            null,
            null,
            7,
            false);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
