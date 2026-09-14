// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;

namespace DvmConsole.iOS;

internal static class IosInputPreferencesDiagnostics
{
    public static async Task<string> RunAsync()
    {
        // Random managed identities isolate this check from all real configurations.
        ConfigurationId first = ConfigurationId.New();
        ConfigurationId second = ConfigurationId.New();
        var preferences = new IosInputPreferences();
        try
        {
            preferences.Write(first, "qualification-usb");
            preferences.Write(second, "qualification-headset");
            var reopened = new IosInputPreferences();
            if (reopened.Read(first) != "qualification-usb" || reopened.Read(second) != "qualification-headset")
                throw new InvalidOperationException("Microphone settings did not reopen independently by configuration.");
            reopened.Write(first, null);
            if (preferences.Read(first) is not null || preferences.Read(second) != "qualification-headset")
                throw new InvalidOperationException("System default changed another configuration's microphone choice.");
        }
        finally { preferences.Write(first, null); preferences.Write(second, null); }
        string result = await IosAudioDiagnostics.RunInputSelectionAsync();
        return result + " Per-configuration microphone preference reopen and independent reset passed.";
    }
}
