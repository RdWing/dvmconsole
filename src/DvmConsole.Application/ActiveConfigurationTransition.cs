namespace DvmConsole.Application;

/// <summary>
/// Keeps the authoritative active revision synchronized with session
/// publication. A failure before publication restores the prior reference;
/// cleanup failures after publication retain the newly published reference.
/// </summary>
public sealed class ActiveConfigurationTransition
{
    private readonly IActiveConfigurationService activeConfigurations;

    public ActiveConfigurationTransition(IActiveConfigurationService activeConfigurations)
    {
        this.activeConfigurations = activeConfigurations ??
            throw new ArgumentNullException(nameof(activeConfigurations));
    }

    public async ValueTask PublishAsync(
        ConfigurationReference configuration,
        Func<CancellationToken, ValueTask> publish,
        Func<bool> publicationSucceeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(publicationSucceeded);

        ConfigurationReference? previous = activeConfigurations.Active;
        await activeConfigurations.ActivateAsync(configuration, cancellationToken).ConfigureAwait(false);
        try
        {
            await publish(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception publicationFailure)
        {
            if (publicationSucceeded())
                throw;

            try
            {
                if (previous is null)
                {
                    await activeConfigurations.DeactivateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await activeConfigurations.ActivateAsync(previous, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackFailure)
            {
                throw new InvalidOperationException(
                    "The session replacement failed and the previous active configuration could not be restored.",
                    new AggregateException(publicationFailure, rollbackFailure));
            }
            throw;
        }
    }
}
