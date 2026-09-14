// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace DvmConsole.Mobile;

/// <summary>Compiled touch styles shared by the mobile Studio surfaces.</summary>
public sealed partial class MobileStudioStyles : Styles
{
    public MobileStudioStyles() => AvaloniaXamlLoader.Load(this);
}
