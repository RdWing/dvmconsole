// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Mobile;
using Foundation;

namespace DvmConsole.iOS;

internal static class IosLayoutPreferencesDiagnostics
{
    private static readonly string[] ReorderedChannels = ["channel-two", "channel-one"];

    public static Task<string> RunAsync()
    {
        string configuration = "layout-diagnostic-" + Guid.NewGuid().ToString("N");
        string other = configuration + "-other";
        try
        {
            var preferences = new IosLayoutPreferences();
            preferences.WriteCardPositions(configuration, new Dictionary<string, MobileCardPosition>
                { ["channel-one"] = new(710, 530), ["channel-two"] = new(0, 0) });
            preferences.WriteListOrder(configuration, ReorderedChannels);
            var restored = new IosLayoutPreferences();
            var positions = restored.ReadCardPositions(configuration);
            if (positions.Count != 2 || positions["channel-one"] != new MobileCardPosition(710, 530) ||
                positions["channel-two"] != new MobileCardPosition(0, 0))
                throw new InvalidOperationException("Native card coordinates did not round trip.");
            if (!restored.ReadListOrder(configuration).SequenceEqual(ReorderedChannels))
                throw new InvalidOperationException("Native list order did not round trip.");
            if (restored.ReadCardPositions(other).Count != 0 || restored.ReadListOrder(other).Count != 0)
                throw new InvalidOperationException("Layout preferences crossed configuration boundaries.");
            return Task.FromResult("PASS\nCard coordinates and independent list insertion order restored from NSUserDefaults.\nConfiguration isolation verified.\n");
        }
        finally
        {
            NSUserDefaults.StandardUserDefaults.RemoveObject($"console-card-positions.v1.{configuration}");
            NSUserDefaults.StandardUserDefaults.RemoveObject($"console-list-order.v1.{configuration}");
        }
    }
}
