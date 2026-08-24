using System.Collections.Specialized;

namespace DvmConsole.Desktop;

internal sealed class MainWindowSessionHost : IAsyncDisposable
{
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly CollectionChangedSubscription activityHistorySubscription;
    private readonly Action<MainWindowViewModel> setDataContext;
    private readonly Action closeSessionWindows;
    private readonly Action closeAllWindows;
    private readonly AsyncDisposal disposal = new();
    private MainWindowViewModel viewModel;
    private CardPttController cardPtt;

    public MainWindowSessionHost(
        MainWindowViewModel initialViewModel,
        NotifyCollectionChangedEventHandler activityHistoryChanged,
        Action<MainWindowViewModel> setDataContext,
        Action closeSessionWindows,
        Action closeAllWindows)
    {
        viewModel = initialViewModel ?? throw new ArgumentNullException(nameof(initialViewModel));
        ArgumentNullException.ThrowIfNull(activityHistoryChanged);
        this.setDataContext = setDataContext ?? throw new ArgumentNullException(nameof(setDataContext));
        this.closeSessionWindows = closeSessionWindows ?? throw new ArgumentNullException(nameof(closeSessionWindows));
        this.closeAllWindows = closeAllWindows ?? throw new ArgumentNullException(nameof(closeAllWindows));
        cardPtt = CreateCardPtt(viewModel);
        activityHistorySubscription = new CollectionChangedSubscription(
            (INotifyCollectionChanged)viewModel.ActivityCallHistory,
            activityHistoryChanged);
        this.setDataContext(viewModel);
    }

    public MainWindowViewModel ViewModel => viewModel;
    public CardPttController CardPtt => cardPtt;

    public ValueTask StartAsync()
        => viewModel.StartKeyboardPttAsync();

    public async Task ReplaceAsync(MainWindowViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await transitionGate.WaitAsync();
        try
        {
            MainWindowViewModel previous = viewModel;
            closeSessionWindows();
            await cardPtt.DisposeAsync();
            activityHistorySubscription.Rebind(
                (INotifyCollectionChanged)replacement.ActivityCallHistory);
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
            cleanup.Run(activityHistorySubscription.Dispose);
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
