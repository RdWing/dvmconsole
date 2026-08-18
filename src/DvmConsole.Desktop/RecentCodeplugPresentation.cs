namespace DvmConsole.Desktop;

internal sealed record RecentCodeplugPresentation(
    string FullPath,
    string FileName,
    string ParentPath)
{
    public static RecentCodeplugPresentation FromPath(string path, int parentBudget = 72)
    {
        string fullPath = path ?? string.Empty;
        int separator = Math.Max(fullPath.LastIndexOf('/'), fullPath.LastIndexOf('\\'));
        string fileName = separator >= 0 ? fullPath[(separator + 1)..] : fullPath;
        string parent = separator > 0 ? fullPath[..separator] : separator == 0 ? fullPath[..1] : string.Empty;
        return new RecentCodeplugPresentation(fullPath, fileName, ElideMiddle(parent, parentBudget));
    }

    private static string ElideMiddle(string value, int budget)
    {
        if (budget < 3)
            throw new ArgumentOutOfRangeException(nameof(budget));
        if (value.Length <= budget)
            return value;

        int available = budget - 1;
        int prefixLength = available / 2;
        int suffixLength = available - prefixLength;
        return $"{value[..prefixLength]}…{value[^suffixLength..]}";
    }
}
