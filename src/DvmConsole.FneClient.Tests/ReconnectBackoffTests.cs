using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class ReconnectBackoffTests
{
    [Fact]
    public void RetriesNormallyOnceThenBacksOffToOneMinute()
    {
        var backoff = new ReconnectBackoff();
        TimeSpan normalRetry = TimeSpan.FromSeconds(5);

        TimeSpan[] delays = Enumerable.Range(0, 7)
            .Select(_ => backoff.NextDelay(normalRetry))
            .ToArray();

        Assert.Equal(
            [
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(40),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60),
                TimeSpan.FromSeconds(60)
            ],
            delays);
    }

    [Fact]
    public void SuccessfulConnectionResetsNormalRetryInterval()
    {
        var backoff = new ReconnectBackoff();
        TimeSpan normalRetry = TimeSpan.FromSeconds(5);
        _ = backoff.NextDelay(normalRetry);
        _ = backoff.NextDelay(normalRetry);
        _ = backoff.NextDelay(normalRetry);

        backoff.Reset();

        Assert.Equal(normalRetry, backoff.NextDelay(normalRetry));
    }

    [Fact]
    public void BackoffNeverShortensALongerConfiguredInterval()
    {
        var backoff = new ReconnectBackoff();
        TimeSpan configuredInterval = TimeSpan.FromSeconds(90);

        Assert.Equal(configuredInterval, backoff.NextDelay(configuredInterval));
        Assert.Equal(configuredInterval, backoff.NextDelay(configuredInterval));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiresAPositiveNormalRetryInterval(int seconds)
    {
        var backoff = new ReconnectBackoff();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            backoff.NextDelay(TimeSpan.FromSeconds(seconds)));
    }
}
