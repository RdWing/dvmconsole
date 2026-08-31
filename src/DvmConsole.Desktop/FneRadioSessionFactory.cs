using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using System.Collections.ObjectModel;

namespace DvmConsole.Desktop;

/// <summary>
/// Binds one immutable application system descriptor to the concrete desktop
/// FNE implementation. Secrets remain in the host-owned connection options and
/// are deliberately excluded from the descriptor.
/// </summary>
internal sealed class FneRadioSessionFactory : IRadioSessionFactory
{
    private readonly FneConnectionOptions options;
    private readonly Func<IReadOnlyCollection<TransmitChannelDescriptor>> getChannels;

    public FneRadioSessionFactory(
        FneConnectionOptions options,
        Func<IReadOnlyCollection<TransmitChannelDescriptor>> getChannels)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.getChannels = getChannels ?? throw new ArgumentNullException(nameof(getChannels));
    }

    public RadioSystemDescriptor Descriptor => CreateDescriptor(options);

    public ValueTask<IRadioSession> CreateAsync(
        RadioSystemDescriptor system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        cancellationToken.ThrowIfCancellationRequested();

        if (system.Id != SystemId.FromName(options.Name) ||
            !system.Protocol.Equals("FNE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The FNE radio factory is not bound to system '{system.Name}' ({system.Id}).",
                nameof(system));
        }

        return ValueTask.FromResult<IRadioSession>(
            new FneRadioSessionAdapter(options, getChannels));
    }

    private static RadioSystemDescriptor CreateDescriptor(FneConnectionOptions options)
        => new(
            SystemId.FromName(options.Name),
            options.Name,
            "FNE",
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["address"] = options.Address,
                    ["port"] = options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["peerId"] = options.PeerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["identity"] = options.Identity,
                    ["encrypted"] = options.Encrypted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["transportEncryption"] = options.TransportEncryptionMode.ToString()
                }));
}
