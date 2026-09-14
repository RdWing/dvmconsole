// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Foundation;

namespace DvmConsole.iOS;

/// <summary>Device-local microphone choice, scoped to a managed configuration identity.</summary>
internal sealed class IosInputPreferences(NSUserDefaults? defaults = null)
{
    private readonly NSUserDefaults settings = defaults ?? NSUserDefaults.StandardUserDefaults;

    public string? Read(ConfigurationId configurationId) => settings.StringForKey(Key(configurationId));

    public void Write(ConfigurationId configurationId, string? inputId)
    {
        if (inputId is null) settings.RemoveObject(Key(configurationId));
        else settings.SetString(inputId, Key(configurationId));
    }

    private static string Key(ConfigurationId configurationId) => $"console-input.v1.{configurationId.Value:D}";
}
