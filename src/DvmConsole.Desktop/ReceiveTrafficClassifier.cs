// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Desktop;

// Centralizes receive-frame lifecycle classification so UI state, priority
// routing, decoder ordering, and audio cleanup cannot drift independently.
internal static class ReceiveTrafficClassifier
{
    public static bool IsTerminator(IRadioMediaFrame traffic)
        => RadioReceiveTrafficClassifier.IsTerminator(traffic);

    public static bool IsDefinitiveStart(IRadioMediaFrame traffic)
        => RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic);

    public static bool IsDmrPrivacyHeader(IRadioMediaFrame traffic)
        => RadioReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic);

    public static bool CarriesVoicePayload(IRadioMediaFrame traffic)
        => RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic);

    public static bool CarriesEncodedVoicePayload(IRadioMediaFrame traffic)
        => RadioReceiveTrafficClassifier.CarriesEncodedVoicePayload(traffic);

    public static ReceiveJitterPacketKind GetJitterPacketKind(IRadioMediaFrame traffic)
        => RadioReceiveTrafficClassifier.GetJitterPacketKind(traffic);

    public static bool IsVoiceFrame(string frameType)
        => RadioReceiveTrafficClassifier.IsVoiceFrame(frameType);
}
