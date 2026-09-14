// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// FNE-specific compatibility adapter; protocol labels live in Application.
internal static class EncryptionPresentation
{
    public static FneTrafficProtocol ParseProtocol(string? protocol)
        => ToFneProtocol(EncryptionProtocolLabels.ParseProtocol(protocol));
    public static string StatusText(bool secure, FneTrafficProtocol protocol, byte? algorithmId)
        => EncryptionProtocolLabels.StatusText(secure, ToMediaProtocol(protocol), algorithmId);
    public static string AlgorithmAbbreviation(FneTrafficProtocol protocol, byte? algorithmId)
        => EncryptionProtocolLabels.AlgorithmAbbreviation(ToMediaProtocol(protocol), algorithmId);
    public static string AlgorithmAbbreviation(RadioMediaProtocol protocol, byte? algorithmId)
        => EncryptionProtocolLabels.AlgorithmAbbreviation(protocol, algorithmId);
    public static string AlgorithmDisplayName(FneTrafficProtocol protocol, byte? algorithmId)
        => EncryptionProtocolLabels.AlgorithmDisplayName(ToMediaProtocol(protocol), algorithmId);
    public static bool TryParseAlgorithmAbbreviation(FneTrafficProtocol protocol, string? abbreviation, out byte algorithmId)
        => EncryptionProtocolLabels.TryParseAlgorithmAbbreviation(ToMediaProtocol(protocol), abbreviation, out algorithmId);
    public static bool TryParseConfiguredAlgorithm(ChannelRuntimeDefinition definition, out byte algorithmId, out ushort keyId)
        => EncryptionProtocolLabels.TryParseConfiguredAlgorithm(definition, out algorithmId, out keyId);
    public static FneTrafficProtocol ToFneProtocol(RadioMediaProtocol protocol) => protocol switch
    {
        RadioMediaProtocol.Dmr => FneTrafficProtocol.Dmr,
        RadioMediaProtocol.P25 => FneTrafficProtocol.P25,
        RadioMediaProtocol.Nxdn => FneTrafficProtocol.Nxdn,
        RadioMediaProtocol.Analog => FneTrafficProtocol.Analog,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };
    public static RadioMediaProtocol ToMediaProtocol(FneTrafficProtocol protocol) => protocol switch
    {
        FneTrafficProtocol.Dmr => RadioMediaProtocol.Dmr,
        FneTrafficProtocol.P25 => RadioMediaProtocol.P25,
        FneTrafficProtocol.Nxdn => RadioMediaProtocol.Nxdn,
        FneTrafficProtocol.Analog => RadioMediaProtocol.Analog,
        _ => (RadioMediaProtocol)(-1)
    };
}
