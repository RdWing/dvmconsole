// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal sealed record ConfigurationStudioKeyEditorState(
    IReadOnlyList<EncryptionAlgorithmOption> Algorithms,
    EncryptionAlgorithmOption? SelectedAlgorithm,
    string KeyIdHexDigits);

internal static class ConfigurationStudioKeyEditor
{
    public static ConfigurationStudioKeyEditorState BuildState(
        KeyEntry? key,
        bool normalizeUnsupported)
    {
        IReadOnlyList<EncryptionAlgorithmOption> algorithms =
            EncryptionAlgorithmCatalog.ForKeyProtocol(key?.Protocol);
        EncryptionAlgorithmOption? selected = key is null
            ? null
            : EncryptionAlgorithmCatalog.FindKeyOption(key.Protocol, key.AlgId);
        if (selected is null && normalizeUnsupported)
        {
            selected = algorithms.Count > 0 ? algorithms[0] : null;
            if (key is not null && selected?.AlgorithmId is int algorithmId)
                key.AlgId = algorithmId;
        }

        return new ConfigurationStudioKeyEditorState(
            algorithms,
            selected,
            key?.KeyId.ToString("X", CultureInfo.InvariantCulture) ?? string.Empty);
    }

    public static bool TryParseKeyId(string value, out ushort keyId)
        => ushort.TryParse(
            value,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out keyId);
}
