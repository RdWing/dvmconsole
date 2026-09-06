// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

internal enum ChannelSelectionKind { Receive, Recording }

internal readonly record struct ChannelSelectionChange(
    ChannelSelectionKind Kind, bool Previous, bool Current, string Origin);
