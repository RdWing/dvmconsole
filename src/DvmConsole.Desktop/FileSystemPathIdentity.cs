namespace DvmConsole.Desktop;

internal static class FileSystemPathIdentity
{
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static bool AreEquivalent(string first, string second)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), Comparison);
    }

    public static bool IsUnderRoot(string rootPath, string path)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, Comparison);
    }
}
