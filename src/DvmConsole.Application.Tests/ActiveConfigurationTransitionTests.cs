using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ActiveConfigurationTransitionTests
{
    [Fact]
    public async Task FailureBeforePublicationRestoresPreviousRevision()
    {
        ConfigurationReference previous = Reference();
        ConfigurationReference replacement = Reference();
        var service = new FakeActiveConfigurationService(previous);
        var transition = new ActiveConfigurationTransition(service);
        var failure = new IOException("publication failed");

        IOException observed = await Assert.ThrowsAsync<IOException>(async () =>
            await transition.PublishAsync(
                replacement,
                _ => ValueTask.FromException(failure),
                () => false));

        Assert.Same(failure, observed);
        Assert.Equal(previous, service.Active);
        Assert.Equal([replacement, previous], service.Activations);
    }

    [Fact]
    public async Task FailureBeforeFirstPublicationDeactivatesReplacement()
    {
        ConfigurationReference replacement = Reference();
        var service = new FakeActiveConfigurationService(active: null);
        var transition = new ActiveConfigurationTransition(service);

        await Assert.ThrowsAsync<IOException>(async () =>
            await transition.PublishAsync(
                replacement,
                _ => ValueTask.FromException(new IOException("publication failed")),
                () => false));

        Assert.Null(service.Active);
        Assert.Equal(1, service.DeactivationCount);
    }

    [Fact]
    public async Task CancellationDuringPublicationStillRestoresPreviousRevision()
    {
        ConfigurationReference previous = Reference();
        ConfigurationReference replacement = Reference();
        var service = new FakeActiveConfigurationService(previous);
        var transition = new ActiveConfigurationTransition(service);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transition.PublishAsync(
                replacement,
                _ =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromCanceled(cancellation.Token);
                },
                () => false,
                cancellation.Token));

        Assert.Equal(previous, service.Active);
    }

    [Fact]
    public async Task CleanupFailureAfterPublicationRetainsReplacementRevision()
    {
        ConfigurationReference previous = Reference();
        ConfigurationReference replacement = Reference();
        var service = new FakeActiveConfigurationService(previous);
        var transition = new ActiveConfigurationTransition(service);

        await Assert.ThrowsAsync<IOException>(async () =>
            await transition.PublishAsync(
                replacement,
                _ => ValueTask.FromException(new IOException("outgoing cleanup failed")),
                () => true));

        Assert.Equal(replacement, service.Active);
        Assert.Equal([replacement], service.Activations);
    }

    [Fact]
    public async Task RollbackFailureReportsPublicationAndRollbackFailures()
    {
        ConfigurationReference previous = Reference();
        ConfigurationReference replacement = Reference();
        var publicationFailure = new IOException("publication failed");
        var rollbackFailure = new UnauthorizedAccessException("rollback failed");
        var service = new FakeActiveConfigurationService(previous)
        {
            ActivationFailure = (reference, call) => call == 2 && reference == previous
                ? rollbackFailure
                : null
        };
        var transition = new ActiveConfigurationTransition(service);

        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transition.PublishAsync(
                replacement,
                _ => ValueTask.FromException(publicationFailure),
                () => false));

        AggregateException aggregate = Assert.IsType<AggregateException>(observed.InnerException);
        Assert.Contains(publicationFailure, aggregate.InnerExceptions);
        Assert.Contains(rollbackFailure, aggregate.InnerExceptions);
    }

    private static ConfigurationReference Reference()
        => new(ConfigurationId.New(), ConfigurationRevision.New());

    private sealed class FakeActiveConfigurationService(ConfigurationReference? active)
        : IActiveConfigurationService
    {
        private int activationCalls;

        public ConfigurationReference? Active { get; private set; } = active;
        public List<ConfigurationReference> Activations { get; } = [];
        public int DeactivationCount { get; private set; }
        public Func<ConfigurationReference, int, Exception?>? ActivationFailure { get; init; }

        public ValueTask ActivateAsync(
            ConfigurationReference configuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = ++activationCalls;
            Exception? failure = ActivationFailure?.Invoke(configuration, call);
            if (failure is not null)
                return ValueTask.FromException(failure);
            Activations.Add(configuration);
            Active = configuration;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeactivationCount++;
            Active = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
