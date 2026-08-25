namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal void SetSpacePttInputSuppressed(bool suppressed)
        => pttSession.SetSpaceInputSuppressed(suppressed);
}
