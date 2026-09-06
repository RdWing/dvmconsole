// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingPathContainmentTests
{
    [Fact]
    public void CatalogDoesNotTreatDifferentlyCasedSiblingAsChildOnCaseSensitiveFilesystem()
    {
        string parent = Path.Combine(Path.GetTempPath(), $"recording-roots-{Guid.NewGuid():N}");
        string root = Path.Combine(parent, "calls");
        string sibling = Path.Combine(parent, "CALLS");
        Directory.CreateDirectory(root);
        if (Directory.Exists(sibling))
        {
            Directory.Delete(parent, recursive: true);
            return;
        }
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

    [Fact]
    public void PathIdentityMatchesContainingFilesystemCaseBehavior()
    {
        string root = Path.Combine(Path.GetTempPath(), $"path-identity-{Guid.NewGuid():N}");
        string lower = Path.Combine(root, "call.opus");
        string upper = Path.Combine(root, "CALL.opus");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(lower, [0]);
            bool filesystemIgnoresCase = File.Exists(upper);

            Assert.Equal(
                filesystemIgnoresCase,
                FileSystemPathIdentity.AreEquivalent(lower, upper));
            Assert.Equal(
                filesystemIgnoresCase,
                FileSystemPathIdentity.Comparer.Equals(lower, upper));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
