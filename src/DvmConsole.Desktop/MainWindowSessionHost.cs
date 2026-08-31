using System.Collections.Specialized;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal sealed class MainWindowSessionHost : IAsyncDisposable
{
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly NotifyCollectionChangedEventHandler activityHistoryChanging;
    private readonly Action<MainWindowViewModel> setDataContext;
    private readonly Action closeSessionWindows;
    private readonly Action closeAllWindows;
    private readonly Func<MainWindowViewModel, CancellationToken, Task> quiesceSession;
    private readonly AsyncDisposal disposal = new();
    private MainWindowViewModel viewModel;
    private IConsoleApplicationSession applicationSession;
    private ChannelPttController cardPtt;

    public MainWindowSessionHost(
        MainWindowViewModel initialViewModel,
        NotifyCollectionChangedEventHandler activityHistoryChanging,
        Action<MainWindowViewModel> setDataContext,
        Action closeSessionWindows,
        Action closeAllWindows,
        Func<MainWindowViewModel, CancellationToken, Task>? quiesceSession = null)
    {
        viewModel = initialViewModel ?? throw new ArgumentNullException(nameof(initialViewModel));
        ArgumentNullException.ThrowIfNull(activityHistoryChanging);
        this.activityHistoryChanging = activityHistoryChanging;
        this.setDataContext = setDataContext ?? throw new ArgumentNullException(nameof(setDataContext));
        this.closeSessionWindows = closeSessionWindows ?? throw new ArgumentNullException(nameof(closeSessionWindows));
        this.closeAllWindows = closeAllWindows ?? throw new ArgumentNullException(nameof(closeAllWindows));
        this.quiesceSession = quiesceSession ?? QuiesceSessionAsync;
        applicationSession = CreateApplicationSession(viewModel);
        cardPtt = CreateChannelPtt(applicationSession);
        viewModel.ActivityCallHistoryChanging += activityHistoryChanging;
        this.setDataContext(viewModel);
    }

    public MainWindowViewModel ViewModel => viewModel;
    public IConsoleApplicationSession ApplicationSession => applicationSession;
    public ChannelPttController ChannelPtt => cardPtt;

    public ValueTask StartAsync()
        => viewModel.StartKeyboardPttAsync();

    // A replacement session reloads operator settings from the shared store.
    // Flush the outgoing session before constructing that replacement so two
    // session-owned writers never race over different settings snapshots.
    public async Task PrepareForReplacementAsync(CancellationToken cancellationToken = default)
    {
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            await applicationSession.FlushSettingsAsync(cancellationToken);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public async Task ReplaceAsync(
        MainWindowViewModel replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            MainWindowViewModel previous = viewModel;
            IConsoleApplicationSession previousApplicationSession = applicationSession;
            ChannelPttController previousCardPtt = cardPtt;
            IConsoleApplicationSession replacementApplicationSession = CreateApplicationSession(replacement);
            ChannelPttController replacementCardPtt = CreateChannelPtt(replacementApplicationSession);
            replacement.ActivityCallHistoryChanging += activityHistoryChanging;
            try
            {
                await replacement.StartKeyboardPttAsync(cancellationToken);
                // The outgoing session can take time to release audio,
                // recording, and presentation resources. Close its FNE
                // transports before publishing the replacement so the same
                // peer identity can never be active from two local sockets.
                await previousApplicationSession.QuiesceAsync(cancellationToken);
                closeSessionWindows();
                setDataContext(replacement);
            }
            catch
            {
                replacement.ActivityCallHistoryChanging -= activityHistoryChanging;
                await replacementApplicationSession.DisposeAsync();
                await replacementCardPtt.DisposeAsync();
                await replacement.DisposeAsync();
                throw;
            }

            // Ownership changes only after the replacement is ready and the
            // window has accepted it. Cleanup failures from the outgoing
            // session cannot leave the host pointing at a half-installed one.
            viewModel = replacement;
            applicationSession = replacementApplicationSession;
            cardPtt = replacementCardPtt;
            previous.ActivityCallHistoryChanging -= activityHistoryChanging;

            var cleanup = new AsyncCleanup();
            await cleanup.RunTaskAsync(() => previousApplicationSession.DisposeAsync().AsTask());
            await cleanup.RunTaskAsync(() => previousCardPtt.DisposeAsync().AsTask());
            await cleanup.RunTaskAsync(() => previous.DisposeAsync().AsTask());
            cleanup.ThrowIfFailed();
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        await transitionGate.WaitAsync();
        try
        {
            var cleanup = new AsyncCleanup();
            // Stop network sessions before the rest of the session graph. In
            // particular, this cancels a failed peer's login backoff without
            // making application shutdown wait for the current retry interval.
            await cleanup.RunTaskAsync(
                () => applicationSession.QuiesceAsync(CancellationToken.None).AsTask());
            cleanup.Run(closeAllWindows);
            cleanup.Run(() => viewModel.ActivityCallHistoryChanging -= activityHistoryChanging);
            await cleanup.RunTaskAsync(() => applicationSession.DisposeAsync().AsTask());
            await cleanup.RunTaskAsync(() => cardPtt.DisposeAsync().AsTask());
            await cleanup.RunTaskAsync(() => viewModel.DisposeAsync().AsTask());
            cleanup.ThrowIfFailed();
        }
        finally
        {
            transitionGate.Release();
        }
    }

    private static ChannelPttController CreateChannelPtt(IConsoleApplicationSession session)
        => new(
            (channelId, cancellationToken) => session.Commands.BeginPttAsync(channelId, cancellationToken),
            (channelId, cancellationToken) => session.Commands.EndPttAsync(channelId, cancellationToken));

    private IConsoleApplicationSession CreateApplicationSession(MainWindowViewModel owner)
        => new ConsoleApplicationSession(new DesktopConsoleSessionRuntimeAdapter(
            owner,
            cancellationToken => new ValueTask(quiesceSession(owner, cancellationToken)),
            cancellationToken => new ValueTask(owner.FlushUserSettingsAsync().WaitAsync(cancellationToken))));

    private static Task QuiesceSessionAsync(
        MainWindowViewModel owner,
        CancellationToken cancellationToken)
        => owner.QuiesceFneSessionAsync(cancellationToken);
}
