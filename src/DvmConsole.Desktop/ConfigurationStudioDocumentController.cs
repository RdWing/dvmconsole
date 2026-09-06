// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal delegate Task<bool> ConfigurationStudioConfirmation(
    string title,
    string message,
    string confirmLabel);

// Owns document mutations and their confirmation boundary. Avalonia event
// handlers stay as thin adapters and the existing view-model surface remains
// stable for compiled bindings and capture tests.
internal sealed class ConfigurationStudioDocumentController
{
    private readonly ConfigurationStudioViewModel viewModel;
    private int deleteSystemOperationInProgress;

    public ConfigurationStudioDocumentController(ConfigurationStudioViewModel viewModel)
        => this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    public async Task ExecuteAsync(
        ConfigurationStudioEditCommand command,
        ConfigurationStudioConfirmation confirm,
        IEnumerable<ChannelConfiguration> selectedChannels)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(selectedChannels);
        switch (command)
        {
            case ConfigurationStudioEditCommand.AddChannel:
                viewModel.AddChannel();
                break;
            case ConfigurationStudioEditCommand.DuplicateChannel:
                viewModel.DuplicateChannel();
                break;
            case ConfigurationStudioEditCommand.DeleteChannel:
                if (viewModel.SelectedChannel is { } channel &&
                    await confirm(
                        "Delete channel",
                        $"Delete '{channel.Name}'? Saved widget and group references to this channel will be removed when the draft is saved.",
                        "Delete"))
                {
                    viewModel.DeleteChannel();
                }
                break;
            case ConfigurationStudioEditCommand.MoveChannelUp:
                viewModel.MoveChannel(-1);
                break;
            case ConfigurationStudioEditCommand.MoveChannelDown:
                viewModel.MoveChannel(1);
                break;
            case ConfigurationStudioEditCommand.ApplySelectedCardSize:
                viewModel.ApplySelectedCardSize(selectedChannels);
                break;
            case ConfigurationStudioEditCommand.SetSelectedRowsRxOnly:
                viewModel.SetChannelsRxOnly(selectedChannels, rxOnly: true);
                break;
            case ConfigurationStudioEditCommand.SetSelectedRowsTxCapable:
                viewModel.SetChannelsRxOnly(selectedChannels, rxOnly: false);
                break;
            case ConfigurationStudioEditCommand.AddZone:
                viewModel.AddZone();
                break;
            case ConfigurationStudioEditCommand.DuplicateZone:
                viewModel.DuplicateZone();
                break;
            case ConfigurationStudioEditCommand.DeleteZone:
                if (viewModel.SelectedZone is { } zone &&
                    await confirm(
                        "Delete zone",
                        $"Delete '{zone.Name}' and its {zone.Channels.Count} channel(s) and {zone.WebStreams.Count} stream(s)?",
                        "Delete"))
                {
                    viewModel.DeleteZone();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }

    public async Task DeleteSelectedSystemAsync(ConfigurationStudioConfirmation confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref deleteSystemOperationInProgress, 1) != 0)
            return;
        try
        {
            if (viewModel.SelectedSystem is { } system &&
                await confirm(
                    "Delete system",
                    $"Delete '{system.Name}'? Channels that reference it will be reported as errors until reassigned.",
                    "Delete"))
            {
                viewModel.DeleteSystem(system);
            }
        }
        finally
        {
            Volatile.Write(ref deleteSystemOperationInProgress, 0);
        }
    }

    public async Task DeleteSelectedStreamAsync(ConfigurationStudioConfirmation confirm)
    {
        if (viewModel.SelectedStream is { } row &&
            await confirm(
                "Delete web stream",
                $"Delete '{row.Stream.Name}' from zone '{row.Zone.Name}'?",
                "Delete"))
        {
            viewModel.DeleteStream();
        }
    }

    public async Task DeleteSelectedGroupAsync(ConfigurationStudioConfirmation confirm)
    {
        if (viewModel.SelectedGroup is { } group &&
            await confirm(
                "Delete group",
                $"Delete '{group.Name}'? Its codeplug-scoped membership, direction, and enabled state will be removed when saved.",
                "Delete"))
        {
            viewModel.DeleteGroup();
        }
    }

    public async Task DeleteSelectedKeyAsync(ConfigurationStudioConfirmation confirm)
    {
        if (viewModel.SelectedKey is { } key &&
            await confirm(
                "Delete encryption key",
                $"Delete {key.Protocol.ToUpperInvariant()} key {key.KeyId}? Channels that reference it may no longer decrypt or transmit securely.",
                "Delete"))
        {
            viewModel.DeleteKey();
        }
    }

    public async Task DeleteSelectedAliasAsync(ConfigurationStudioConfirmation confirm)
    {
        if (viewModel.SelectedAlias is { } row &&
            await confirm(
                "Delete RID alias",
                $"Delete RID {row.Alias.Rid} ({row.Alias.Alias}) from its alias file?",
                "Delete"))
        {
            viewModel.DeleteAlias();
        }
    }
}
