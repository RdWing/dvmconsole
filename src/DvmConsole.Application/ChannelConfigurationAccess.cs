// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Application;

/// <summary>Configuration and key availability shared by channel controls and transmit admission.</summary>
public sealed class ChannelConfigurationAccess
{
    private readonly ChannelRuntimeDefinition definition;
    private readonly IP25KeyResolver? p25Keys;
    private readonly IDmrKeyResolver? dmrKeys;
    private readonly INxdnKeyResolver? nxdnKeys;

    public ChannelConfigurationAccess(ChannelRuntimeDefinition definition,
        IP25KeyResolver? p25Keys = null, IDmrKeyResolver? dmrKeys = null, INxdnKeyResolver? nxdnKeys = null)
    {
        this.definition = definition ?? throw new ArgumentNullException(nameof(definition));
        this.p25Keys = p25Keys;
        this.dmrKeys = dmrKeys;
        this.nxdnKeys = nxdnKeys;
    }

    public bool CanListen => definition.Protocol switch
    {
        ChannelProtocol.Dmr or ChannelProtocol.P25 or ChannelProtocol.Nxdn => true,
        ChannelProtocol.Analog => !definition.IsEncrypted,
        _ => false
    };

    public bool ConfiguredKeyAvailable => definition.Protocol switch
    {
        ChannelProtocol.P25 => p25Keys?.CanResolve(definition.SystemName,
            definition.EncryptionAlgorithm, definition.EncryptionKeyId) == true,
        ChannelProtocol.Dmr => dmrKeys?.CanResolve(definition.SystemName,
            definition.EncryptionAlgorithm, definition.EncryptionKeyId) == true,
        ChannelProtocol.Nxdn => nxdnKeys?.CanResolve(definition.SystemName,
            definition.EncryptionAlgorithm, definition.EncryptionKeyId) == true,
        _ => false
    };

    public bool TransmitKeyAvailable => !definition.IsEncrypted || ConfiguredKeyAvailable;

    public bool CanToggleEncryption(bool transmitEncrypted)
        => ChannelProtocolMediaMapper.RequiresVocoder(definition.Protocol) &&
           definition.IsEncrypted && definition.SelectableEncryption &&
           (transmitEncrypted || ConfiguredKeyAvailable);

    public bool CanChangeEncryption(ChannelOperatorSnapshot operation, bool encrypted)
        => operation.TransmitEncrypted != encrypted &&
           !operation.TransmitStarting && !operation.TransmitEnabled && !operation.TransmitStopping &&
           CanToggleEncryption(operation.TransmitEncrypted);

    public bool CanTransmit(bool transmitEncrypted)
        => !definition.RxOnly && definition.Protocol switch
        {
            ChannelProtocol.Dmr or ChannelProtocol.P25 or ChannelProtocol.Nxdn =>
                !transmitEncrypted || ConfiguredKeyAvailable,
            ChannelProtocol.Analog => !definition.IsEncrypted,
            _ => false
        };

    public string TransmitUnavailableReason(bool transmitEncrypted)
        => definition.RxOnly ? "the channel is receive-only"
            : transmitEncrypted && !ConfiguredKeyAvailable ? "its encryption key is unavailable"
            : "the channel is not available for transmit";

    public string AuthorityUnavailableReason => definition.Protocol == ChannelProtocol.Dmr
        ? $"the FNE does not allow TG {definition.DestinationId} on TS{definition.Slot + 1}"
        : $"the FNE does not allow TG {definition.DestinationId}";
}
