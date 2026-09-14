// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DvmConsole.Mobile;

/// <summary>Compiled, console-scoped resources; no runtime XAML discovery under iOS AOT.</summary>
public sealed partial class MobileConsoleTheme : ResourceDictionary
{
    public MobileConsoleTheme() => AvaloniaXamlLoader.Load(this);
}
