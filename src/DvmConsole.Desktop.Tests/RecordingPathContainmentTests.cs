using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingPathContainmentTests
{
    [Fact]
    public void CatalogDoesNotTreatDifferentlyCasedSiblingAsChildOnCaseSensitivePlatforms()
    {
        if (OperatingSystem.IsWindows())
            return;

        string parent = Path.Combine(Path.GetTempPath(), $"recording-roots-{Guid.NewGuid():N}");
        string root = Path.Combine(parent, "calls");
        string sibling = Path.Combine(parent, "CALLS");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        string outside = Path.Combine(sibling, "outside.opus");
        File.WriteAllBytes(outside, [0]);
        try
        {
            var metadata = new CallRecordingMetadata { FilePath = outside };

            Assert.False(new RecordingCatalogStore().TryGetExistingPath(root, metadata, out _));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
