// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections;

namespace DvmConsole.Presentation;

public interface IGroupSettingsViewModel
{
    IEnumerable PatchGroups { get; }
}

public sealed class PatchGroupEventArgs(PatchGroupEditorViewModel group) : EventArgs
{
    public PatchGroupEditorViewModel Group { get; } = group ?? throw new ArgumentNullException(nameof(group));
}
