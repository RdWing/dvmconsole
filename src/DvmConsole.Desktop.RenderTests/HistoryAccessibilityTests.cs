// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class HistoryAccessibilityTests
{
    [AvaloniaFact]
    public void FilterPeersIdentifyTheirPurposeWhenAllHaveTheSameValue()
    {
        var view = new CallHistoryView { DataContext = new UnifiedCallHistoryViewModel() };
        var window = new Window { Width = 1024, Height = 768, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            view.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
            window.UpdateLayout();
            var names = view.GetVisualDescendants().OfType<ComboBox>()
                .Select(control => ControlAutomationPeer.CreatePeerForElement(control)?.GetName() ?? string.Empty).ToArray();
            Assert.Equal(["Direction filter", "Protocol filter", "Encryption filter"], names);
        }
        finally { window.Close(); }
    }
}
