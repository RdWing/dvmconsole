namespace DvmConsole.Desktop;

// Constructs disposable resources as one ownership unit. If any later item
// fails, already-created items are released in reverse construction order and
// the original failure remains the primary exception.
internal static class OwnedResourceCollectionBuilder
{
    public static IReadOnlyList<T> Create<T>(int count, Func<int, T> create)
        where T : IAsyncDisposable
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        ArgumentNullException.ThrowIfNull(create);

        var resources = new List<T>(count);
        try
        {
            for (int index = 0; index < count; index++)
                resources.Add(create(index));
            return resources;
        }
        catch (Exception constructionException)
        {
            var cleanup = new AsyncCleanup();
            for (int index = resources.Count - 1; index >= 0; index--)
            {
                int ownedIndex = index;
                cleanup.Run(() => resources[ownedIndex]
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult());
            }
            try
            {
                cleanup.ThrowIfFailed();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Resource collection construction and rollback both failed.",
                    constructionException,
                    cleanupException);
            }
            throw;
        }
    }
}
