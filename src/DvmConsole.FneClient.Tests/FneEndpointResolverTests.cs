using System.Net;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FneEndpointResolverTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    public async Task PreservesLiteralAddressesWithoutDns(string input, string expected)
    {
        var resolver = new FneEndpointResolver();

        IPEndPoint endpoint = await resolver.ResolveAsync(input, 62031, CancellationToken.None);

        Assert.Equal(IPAddress.Parse(expected), endpoint.Address);
        Assert.Equal(62031, endpoint.Port);
    }
}
