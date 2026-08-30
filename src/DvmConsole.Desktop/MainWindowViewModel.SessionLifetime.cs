namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    // Session replacement must stop network identity ownership before the new
    // view model becomes reachable from the window. Remaining audio and
    // presentation cleanup may then finish without competing for an FNE peer.
    internal Task QuiesceFneSessionAsync(CancellationToken cancellationToken = default)
        => connectionSession.DisconnectAsync(cancellationToken);
}
