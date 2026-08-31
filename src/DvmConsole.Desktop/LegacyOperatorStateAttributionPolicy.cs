namespace DvmConsole.Desktop;

internal static class LegacyOperatorStateAttributionPolicy
{
    public static bool ShouldAttributeToOpenedConfiguration(
        string? requestedPath,
        string? lastCodeplugPath)
    {
        // No explicit path means the persisted active configuration owns the
        // legacy working state during the one-time upgrade migration.
        if (string.IsNullOrWhiteSpace(requestedPath))
            return true;
        if (string.IsNullOrWhiteSpace(lastCodeplugPath))
            return false;

        try
        {
            return FileSystemPathIdentity.AreEquivalent(requestedPath, lastCodeplugPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
