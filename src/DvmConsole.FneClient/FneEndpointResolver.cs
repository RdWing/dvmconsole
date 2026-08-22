using System.Net;
using System.Net.Sockets;

namespace DvmConsole.FneClient;

internal interface IFneEndpointResolver
{
    Task<IPEndPoint> ResolveAsync(
        string address,
        int port,
        CancellationToken cancellationToken);
}

internal sealed class FneEndpointResolver : IFneEndpointResolver
{
    public async Task<IPEndPoint> ResolveAsync(
        string address,
        int port,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(address, out IPAddress? parsedAddress))
            return new IPEndPoint(parsedAddress, port);

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(address, cancellationToken)
            .ConfigureAwait(false);
        IPAddress? resolved = addresses.FirstOrDefault(
                candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        return resolved is null
            ? throw new InvalidOperationException($"Could not resolve FNE address '{address}'.")
            : new IPEndPoint(resolved, port);
    }
}
