// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns channel renderer preference resolution and the corresponding shell
/// controls. PTT release remains an explicit caller-owned transition that runs
/// before <see cref="Apply"/> when <see cref="RequiresSwitch"/> is true.
/// </summary>
internal sealed class ChannelRendererController
{
    private readonly OperatorViewSettings settings;
    private readonly ContentControl host;
    private readonly Control cardsRenderer;
    private readonly Control listRenderer;
    private readonly MenuItem cardsMenuItem;
    private readonly MenuItem listMenuItem;
    private ConsoleRendererPreference effectiveRenderer;

    public ChannelRendererController(
        OperatorViewSettings settings,
        ContentControl host,
        Control cardsRenderer,
        Control listRenderer,
        MenuItem cardsMenuItem,
        MenuItem listMenuItem)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.cardsRenderer = cardsRenderer ?? throw new ArgumentNullException(nameof(cardsRenderer));
        this.listRenderer = listRenderer ?? throw new ArgumentNullException(nameof(listRenderer));
        this.cardsMenuItem = cardsMenuItem ?? throw new ArgumentNullException(nameof(cardsMenuItem));
        this.listMenuItem = listMenuItem ?? throw new ArgumentNullException(nameof(listMenuItem));
    }

    public ConsoleRendererPreference Preference
    {
        get => settings.ChannelRenderer;
        set => settings.ChannelRenderer = value;
    }

    public bool RequiresSwitch(double logicalWidth)
        => Resolve(logicalWidth).EffectiveRenderer != effectiveRenderer;

    public void Apply(double logicalWidth)
    {
        ResponsivePresentation resolved = Resolve(logicalWidth);
        effectiveRenderer = resolved.EffectiveRenderer;
        if (listRenderer.DataContext is ConsoleListViewModel list)
            list.SetPresentationActive(effectiveRenderer == ConsoleRendererPreference.List);
        host.Content = effectiveRenderer == ConsoleRendererPreference.Cards
            ? cardsRenderer
            : listRenderer;
        cardsMenuItem.IsEnabled =
            effectiveRenderer != ConsoleRendererPreference.Cards &&
            logicalWidth >= ResponsivePresentationPolicy.NarrowMinimum;
        listMenuItem.IsEnabled = effectiveRenderer != ConsoleRendererPreference.List;
        cardsMenuItem.IsChecked = effectiveRenderer == ConsoleRendererPreference.Cards;
        listMenuItem.IsChecked = effectiveRenderer == ConsoleRendererPreference.List;
        ToolTip.SetTip(
            cardsMenuItem,
            logicalWidth < ResponsivePresentationPolicy.NarrowMinimum
                ? "List is required below 600 logical pixels; the saved desktop preference is unchanged."
                : null);
    }

    private ResponsivePresentation Resolve(double logicalWidth)
        => ResponsivePresentationPolicy.Resolve(logicalWidth, settings.ChannelRenderer);
}
