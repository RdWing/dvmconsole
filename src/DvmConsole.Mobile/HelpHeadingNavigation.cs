// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using Avalonia;
using Avalonia.VisualTree;
using ColorDocument.Avalonia;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace DvmConsole.Mobile;

/// <summary>Resolves Markdown heading fragments against rendered text, including duplicate headings.</summary>
internal static class HelpHeadingNavigation
{
    public static bool ScrollToHeading(MarkdownScrollViewer reader, string fragment)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(fragment);
        string target = Uri.UnescapeDataString(fragment.TrimStart('#'));
        if (target.Length == 0) { reader.ScrollValue = default; return true; }
        reader.UpdateLayout();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (CTextBlock heading in reader.GetVisualDescendants().OfType<CTextBlock>())
        {
            if (!IsHeading(heading)) continue;
            string root = Slug(heading.Text);
            string candidate = root;
            for (int suffix = 1; !used.Add(candidate); suffix++) candidate = $"{root}-{suffix}";
            if (candidate != target || heading.TranslatePoint(default, reader) is not { } point) continue;
            reader.ScrollValue = new Vector(reader.ScrollValue.X, Math.Max(0, reader.ScrollValue.Y + point.Y));
            return true;
        }
        return false;
    }

    private static bool IsHeading(CTextBlock text)
        => text.Classes.Contains(ClassNames.Heading1Class) || text.Classes.Contains(ClassNames.Heading2Class) ||
           text.Classes.Contains(ClassNames.Heading3Class) || text.Classes.Contains(ClassNames.Heading4Class) ||
           text.Classes.Contains(ClassNames.Heading5Class) || text.Classes.Contains(ClassNames.Heading6Class);

    private static string Slug(string text)
    {
        var result = new StringBuilder(text.Length);
        Span<char> buffer = stackalloc char[2];
        foreach (Rune rune in text.Trim().ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) result.Append('-');
            else if (Rune.IsLetterOrDigit(rune) || rune.Value is '-' or '_')
                result.Append(buffer[..rune.EncodeToUtf16(buffer)]);
        }
        return result.ToString();
    }
}
