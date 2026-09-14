// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Platform;
using AVKit;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>Embeds the system route chooser without activating audio or requesting capture.</summary>
internal sealed class IosAudioRoutePicker : NativeControlHost
{
    public IosAudioRoutePicker()
    {
        Width = 48;
        Height = 48;
        Avalonia.Automation.AutomationProperties.SetName(this, "Choose audio output");
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        => new UIViewControlHandle(new AVRoutePickerView
        {
            PrioritizesVideoDevices = false,
            TintColor = UIColor.SystemBlue,
            ActiveTintColor = UIColor.SystemBlue,
            AccessibilityLabel = "Choose audio output"
        });
}
