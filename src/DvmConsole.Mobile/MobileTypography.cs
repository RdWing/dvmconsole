// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;

namespace DvmConsole.Mobile;

/// <summary>Host text preferences scale readable controls while each renderer retains its structure.</summary>
public static class MobileTypography
{
    public static void Apply(IResourceDictionary resources, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        resources["MobileTextScale"] = scale;
        resources["MobileNavigationFontSize"] = 16 * scale;
        resources["MobileBodyFontSize"] = 14 * scale;
        resources["MobileHelpHeading1FontSize"] = 26 * scale;
        resources["MobileHelpHeading2FontSize"] = 22 * scale;
        resources["MobileHelpHeading3FontSize"] = 18 * scale;
        resources["StudioSectionFontSize"] = 16 * scale;
        resources["StudioTitleFontSize"] = 15 * scale;
        resources["StudioBodyFontSize"] = 14 * scale;
        resources["ChannelListHeadingFontSize"] = 16 * scale;
        resources["MobileChannelNameFontSize"] = 16 * scale;
        resources["MobileChannelNameLineHeight"] = 24 * scale;
        resources["MobileChannelDetailLineHeight"] = 19.5 * scale;
        resources["ChannelListZoneFontSize"] = 15 * scale;
        resources["ChannelListDetailFontSize"] = 13 * scale;
    }
    public static T WithScaledFontSize<T>(this T text, double baseline) where T : TextBlock
    {
        text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("MobileTextScale",
            value => baseline * (value is double scale ? scale : 1)));
        return text;
    }

}
