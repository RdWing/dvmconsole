// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;

namespace DvmConsole.Presentation;

public enum ConsoleCardSelection { Transmit, Page, Alert, Recording, Encryption }

public enum ConsoleCardActivity { Idle, Listening, Receiving, TransmitStarting, Transmitting }

/// <summary>Operational card colors shared by desktop and mobile renderers.</summary>
public static class ConsoleCardPalette
{
    private static readonly IBrush lightBackground = Brush("#FFFFFF");
    private static readonly IBrush darkBackground = Brush("#151D26");
    private static readonly IBrush lightBorder = Brush("#9BA8B5");
    private static readonly IBrush darkBorder = Brush("#2A3A4B");
    private static readonly IBrush lightText = Brush("#18212B");
    private static readonly IBrush darkText = Brush("#DCE3EB");
    private static readonly IBrush listeningLight = Brush("#E2F3E8");
    private static readonly IBrush listeningDark = Brush("#1B2B22");
    private static readonly IBrush listeningBorder = Brush("#4E8060");
    private static readonly IBrush receivingBackground = Brush("#008238");
    private static readonly IBrush receivingBorder = Brush("#00C86A");
    private static readonly IBrush transmitBackground = Brush("#0B6B9C");
    private static readonly IBrush transmitBorder = Brush("#2497D3");
    private static readonly IBrush startingBorder = Brush("#D99920");
    private static readonly IBrush activeText = Brush("#FFFFFF");

    private static readonly IBrush touchBackground = Brush("#17212B");
    private static readonly IBrush touchListening = Brush("#172E24");
    private static readonly IBrush touchBorder = Brush("#354757");
    private static readonly IBrush touchListeningBorder = Brush("#438566");
    private static readonly IBrush touchText = Brush("#E4EAF1");

    public static IBrush Background(bool dark, ConsoleCardActivity activity, bool touch = false)
        => touch && dark && activity is ConsoleCardActivity.Idle or ConsoleCardActivity.Listening or ConsoleCardActivity.Receiving
            ? activity == ConsoleCardActivity.Idle ? touchBackground : touchListening
            : activity switch
            {
                ConsoleCardActivity.TransmitStarting or ConsoleCardActivity.Transmitting => transmitBackground,
                ConsoleCardActivity.Receiving => receivingBackground,
                ConsoleCardActivity.Listening => dark ? listeningDark : listeningLight,
                _ => Background(dark)
            };
    public static IBrush Border(bool dark, ConsoleCardActivity activity, bool touch = false)
        => touch && dark && activity is ConsoleCardActivity.Idle or ConsoleCardActivity.Listening or ConsoleCardActivity.Receiving
            ? activity == ConsoleCardActivity.Idle ? touchBorder : touchListeningBorder
            : activity switch
            {
                ConsoleCardActivity.TransmitStarting => startingBorder,
                ConsoleCardActivity.Transmitting => transmitBorder,
                ConsoleCardActivity.Receiving => receivingBorder,
                ConsoleCardActivity.Listening => listeningBorder,
                _ => Border(dark)
            };
    public static IBrush Text(bool dark, ConsoleCardActivity activity, bool touch = false)
        => touch && dark ? touchText : activity is ConsoleCardActivity.Receiving or ConsoleCardActivity.TransmitStarting or ConsoleCardActivity.Transmitting
            ? activeText : Text(dark);

    public static IBrush Background(bool dark) => dark ? darkBackground : lightBackground;
    public static IBrush Border(bool dark) => dark ? darkBorder : lightBorder;
    public static IBrush Text(bool dark) => dark ? darkText : lightText;
    private static readonly IReadOnlyDictionary<string, IBrush> selectionColors = new[]
    {
        "#694BB0", "#D7C9F2", "#B69AF4", "#7655B8", "#A15B2A", "#F2D1B8", "#F0A15C", "#A95C26",
        "#8A3D68", "#F0C7DE", "#E58BBC", "#A84479", "#8A3A3A", "#F2CCCC", "#E58A8A", "#A84343",
        "#6746AA", "#AD8EDF", "#893D42", "#DC8389", "#17212B", "#425369",
        "#B45309", "#F59E0B", "#242938", "#E8EDF3", "#3A4555", "#8996A3"
    }.ToDictionary(color => color, Brush);

    public static IBrush Selection(bool dark, ConsoleCardSelection kind, bool selected, bool border = false, bool touch = false)
    {
        if (touch && dark)
        {
            if (!selected) return selectionColors[border ? "#425369" : "#17212B"];
            if (kind == ConsoleCardSelection.Transmit) return selectionColors[border ? "#AD8EDF" : "#6746AA"];
            if (kind == ConsoleCardSelection.Recording) return selectionColors[border ? "#DC8389" : "#893D42"];
        }
        if (!selected) return selectionColors[border ? dark ? "#3A4555" : "#8996A3" : dark ? "#242938" : "#E8EDF3"];
        string color = (kind, border) switch
        {
            (ConsoleCardSelection.Transmit, false) => dark ? "#694BB0" : "#D7C9F2",
            (ConsoleCardSelection.Transmit, true) => dark ? "#B69AF4" : "#7655B8",
            (ConsoleCardSelection.Page, false) => dark ? "#A15B2A" : "#F2D1B8",
            (ConsoleCardSelection.Page, true) => dark ? "#F0A15C" : "#A95C26",
            (ConsoleCardSelection.Alert, false) => dark ? "#8A3D68" : "#F0C7DE",
            (ConsoleCardSelection.Alert, true) => dark ? "#E58BBC" : "#A84479",
            (ConsoleCardSelection.Recording, false) => dark ? "#8A3A3A" : "#F2CCCC",
            (ConsoleCardSelection.Recording, true) => dark ? "#E58A8A" : "#A84343",
            (ConsoleCardSelection.Encryption, false) => "#B45309",
            (ConsoleCardSelection.Encryption, true) => "#F59E0B",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return selectionColors[color];
    }

    public static IBrush EncryptionText(bool dark, bool encrypted) => encrypted ? activeText : Text(dark);

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
