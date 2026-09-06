// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IGroupSettingsViewModel
{
    System.Collections.IEnumerable IGroupSettingsViewModel.PatchGroups => PatchGroups;
}
