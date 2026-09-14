// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class StudioCompanionDiagnosticTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "neo-companion-diagnostics-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedCompanionsReportNamesWithoutWorkspacePaths(bool malformed)
    {
        Directory.CreateDirectory(root);
        if (malformed)
        {
            File.WriteAllText(Path.Combine(root, "aliases.yml"), "[private-fixture:");
            File.WriteAllText(Path.Combine(root, "keys.yml"), "[private-fixture:");
        }
        var document = ConfigurationDocument.Parse("""
            keyFile: ./keys.yml
            systems:
              - name: Test
                address: 127.0.0.1
                port: 62031
                peerId: 1
                rid: "1001"
                aliasPath: ./aliases.yml
            zones: []
            groups: []
            """, Path.Combine(root, "codeplug.yml"));
        var snapshot = new MaterializedConfigurationStudioCompanionSource().Load(document);
        string aliasMessage = Assert.Single(malformed ? snapshot.AliasErrors : snapshot.AliasWarnings);
        Assert.Contains("aliases.yml", aliasMessage);
        Assert.NotNull(snapshot.KeyFile);
        Assert.Contains("keys.yml", snapshot.KeyFile.LoadIssue);
        foreach (string message in new[] { aliasMessage, snapshot.KeyFile.LoadIssue! })
        {
            Assert.DoesNotContain(root, message);
            Assert.DoesNotContain("private-fixture", message);
        }
        Assert.Equal(!malformed, snapshot.KeyFile.LoadIssueIsWarning);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
