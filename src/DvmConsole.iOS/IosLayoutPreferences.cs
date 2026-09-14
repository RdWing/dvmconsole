// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Foundation;

namespace DvmConsole.iOS;

internal sealed class IosLayoutPreferences : IMobileLayoutPreferences, IMobilePttPreferences, IMobileCardOrderPreferences, IMobileCardLayoutPreferences
{
    public IReadOnlyDictionary<string, MobileCardPosition> ReadCardPositions(string configurationId)
    {
        string? json = NSUserDefaults.StandardUserDefaults.StringForKey($"console-card-positions.v1.{configurationId}");
        if (json is null) return new Dictionary<string, MobileCardPosition>();
        try { return System.Text.Json.JsonSerializer.Deserialize(json, CardLayoutJsonContext.Default.DictionaryStringMobileCardPosition)
            ?? new Dictionary<string, MobileCardPosition>(); }
        catch (System.Text.Json.JsonException) { return new Dictionary<string, MobileCardPosition>(); }
    }

    public void WriteCardPositions(string configurationId, IReadOnlyDictionary<string, MobileCardPosition> positions)
        => NSUserDefaults.StandardUserDefaults.SetString(System.Text.Json.JsonSerializer.Serialize(
            positions.ToDictionary(pair => pair.Key, pair => pair.Value), CardLayoutJsonContext.Default.DictionaryStringMobileCardPosition),
            $"console-card-positions.v1.{configurationId}");

    public IReadOnlyList<string> ReadListOrder(string configurationId)
        => NSUserDefaults.StandardUserDefaults.StringArrayForKey($"console-list-order.v1.{configurationId}") ?? [];

    public void WriteListOrder(string configurationId, IReadOnlyList<string> order)
    {
        using var values = NSArray.FromStrings(order.ToArray());
        using var key = new NSString($"console-list-order.v1.{configurationId}");
        NSUserDefaults.StandardUserDefaults.SetValueForKey(values, key);
    }

    public IReadOnlyList<string> ReadCardOrder(string configurationId)
        => NSUserDefaults.StandardUserDefaults.StringArrayForKey($"console-card-order.v1.{configurationId}") ?? [];

    public void WriteCardOrder(string configurationId, IReadOnlyList<string> order)
    {
        using var values = NSArray.FromStrings(order.ToArray());
        using var key = new NSString($"console-card-order.v1.{configurationId}");
        NSUserDefaults.StandardUserDefaults.SetValueForKey(values, key);
    }

    public bool ReadTogglePtt(string configurationId)
        => NSUserDefaults.StandardUserDefaults.BoolForKey(PttKey(configurationId));

    public void WriteTogglePtt(string configurationId, bool enabled)
        => NSUserDefaults.StandardUserDefaults.SetBool(enabled, PttKey(configurationId));

    private static string PttKey(string configurationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        return $"console-ptt-toggle.v1.{configurationId}";
    }

    public ConsoleRendererPreference? Read(string configurationId)
        => NSUserDefaults.StandardUserDefaults.StringForKey(Key(configurationId)) switch
        {
            "cards" => ConsoleRendererPreference.Cards,
            "list" => ConsoleRendererPreference.List,
            _ => null
        };

    public void Write(string configurationId, ConsoleRendererPreference preference)
        => NSUserDefaults.StandardUserDefaults.SetString(preference switch
        {
            ConsoleRendererPreference.Cards => "cards",
            ConsoleRendererPreference.List => "list",
            _ => throw new ArgumentOutOfRangeException(nameof(preference))
        }, Key(configurationId));

    private static string Key(string configurationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        return $"console-layout.v1.{configurationId}";
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, MobileCardPosition>))]
internal partial class CardLayoutJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
