using System.Collections.Specialized;

namespace DvmConsole.Desktop;

internal sealed class MainWindowSessionHost : IAsyncDisposable
{
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly NotifyCollectionChangedEventHandler activityHistoryChanging;
    private readonly Action<MainWindowViewModel> setDataContext;
    private readonly Action closeSessionWindows;
    private readonly Action closeAllWindows;
    private readonly AsyncDisposal disposal = new();
    private MainWindowViewModel viewModel;
    private CardPttController cardPtt;

    public MainWindowSessionHost(
        MainWindowViewModel initialViewModel,
        NotifyCollectionChangedEventHandler activityHistoryChanging,
        Action<MainWindowViewModel> setDataContext,
        Action closeSessionWindows,
        Action closeAllWindows)
    {
        viewModel = initialViewModel ?? throw new ArgumentNullException(nameof(initialViewModel));
        ArgumentNullException.ThrowIfNull(activityHistoryChanging);
        this.activityHistoryChanging = activityHistoryChanging;
        this.setDataContext = setDataContext ?? throw new ArgumentNullException(nameof(setDataContext));
        this.closeSessionWindows = closeSessionWindows ?? throw new ArgumentNullException(nameof(closeSessionWindows));
        this.closeAllWindows = closeAllWindows ?? throw new ArgumentNullException(nameof(closeAllWindows));
        cardPtt = CreateCardPtt(viewModel);
        viewModel.ActivityCallHistoryChanging += activityHistoryChanging;
        this.setDataContext(viewModel);
    }

    public MainWindowViewModel ViewModel => viewModel;
    public CardPttController CardPtt => cardPtt;

    public ValueTask StartAsync()
        => viewModel.StartKeyboardPttAsync();

    // A replacement session reloads operator settings from the shared store.
    // Flush the outgoing session before constructing that replacement so two
    // session-owned writers never race over different settings snapshots.
    public async Task PrepareForReplacementAsync()
    {
        await transitionGate.WaitAsync();
        try
        {
            await viewModel.FlushUserSettingsAsync();
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public async Task ReplaceAsync(MainWindowViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await transitionGate.WaitAsync();
        try
        {
            MainWindowViewModel previous = viewModel;
            closeSessionWindows();
            await cardPtt.DisposeAsync();
            previous.ActivityCallHistoryChanging -= activityHistoryChanging;
            replacement.ActivityCallHistoryChanging += activityHistoryChanging;
            viewModel = replacement;
            cardPtt = CreateCardPtt(replacement);
            setDataContext(replacement);
            await previous.DisposeAsync();
            await replacement.StartKeyboardPttAsync();
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
            cleanup.Run(closeAllWindows);
            cleanup.Run(() => viewModel.ActivityCallHistoryChanging -= activityHistoryChanging);
            await cleanup.RunTaskAsync(() => cardPtt.DisposeAsync().AsTask());
            await cleanup.RunTaskAsync(() => viewModel.DisposeAsync().AsTask());
            cleanup.ThrowIfFailed();
        }
        finally
        {
            transitionGate.Release();
        }
    }

    private static CardPttController CreateCardPtt(MainWindowViewModel owner)
        => new(
            channel => owner.StartChannelTransmitAsync(channel),
            channel => owner.StopChannelTransmitAsync(channel));
}
