using System.Globalization;

namespace DvmConsole.Desktop;

// A demo session never reads from or writes to the operator's application-
// support directory. Its disposable process-scoped root is suitable for both
// packaged demos and deterministic screenshot automation.
internal sealed class DemoSessionState : IDisposable
{
    private readonly string rootPath;
    private int disposed;

    private DemoSessionState(string rootPath)
    {
        this.rootPath = rootPath;
        UserSettingsPath = Path.Combine(rootPath, "UserSettings.json");
        OperatorViewPath = Path.Combine(rootPath, "OperatorView.json");
    }

    public string UserSettingsPath { get; }
    public string OperatorViewPath { get; }

    public static DemoSessionState Create(string? temporaryRoot = null)
    {
        string basePath = string.IsNullOrWhiteSpace(temporaryRoot)
            ? Path.GetTempPath()
            : Path.GetFullPath(temporaryRoot);
        string rootPath = Path.Combine(
            basePath,
            "DvmConsoleNEODemo",
            $"{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        return new DemoSessionState(rootPath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The operating system may still own a native audio or file-picker
            // handle during process shutdown. The isolated temporary directory
            // remains harmless and can be reclaimed by normal temp cleanup.
            DesktopCrashLog.Write("Demo session cleanup", exception);
        }
    }
}
