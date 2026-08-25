using System.Xml.Linq;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void ProductionProjectsKeepTheEstablishedDependencyDirection()
    {
        string sourceRoot = FindSourceRoot();
        var expected = new Dictionary<string, string[]>
        {
            ["DvmConsole.App"] = ["DvmConsole.Core"],
            ["DvmConsole.Audio"] = [],
            ["DvmConsole.Core"] = [],
            ["DvmConsole.Desktop"] =
                ["DvmConsole.Audio", "DvmConsole.Core", "DvmConsole.FneClient", "DvmConsole.Media", "DvmConsole.Operations", "DvmConsole.Vocoder"],
            ["DvmConsole.Fne"] = [],
            ["DvmConsole.FneClient"] = ["DvmConsole.Core", "DvmConsole.Fne"],
            ["DvmConsole.Media"] =
                ["DvmConsole.Audio", "DvmConsole.Core", "DvmConsole.Fne", "DvmConsole.FneClient", "DvmConsole.Vocoder"],
            ["DvmConsole.Operations"] = ["DvmConsole.Core"],
            ["DvmConsole.Vocoder"] = []
        };

        foreach ((string projectName, string[] expectedReferences) in expected)
        {
            string projectPath = Path.Combine(sourceRoot, projectName, $"{projectName}.csproj");
            string[] actualReferences = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension((string?)element.Attribute("Include")))
                .Where(name => name is not null)
                .Order(StringComparer.Ordinal)
                .ToArray()!;

            Assert.Equal(expectedReferences.Order(StringComparer.Ordinal), actualReferences);
        }
    }

    [Fact]
    public void DesktopDoesNotReferenceRawFnecoreTypes()
    {
        string desktopRoot = Path.Combine(FindSourceRoot(), "DvmConsole.Desktop");
        string[] violations = Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string source = File.ReadAllText(path);
                return source.Contains("using fnecore", StringComparison.Ordinal) ||
                       source.Contains("fnecore.", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;

        Assert.Empty(violations);
    }

    private static string FindSourceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (File.Exists(Path.Combine(directory.FullName, "dvmconsole.sln")) && Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository source directory.");
    }
}
