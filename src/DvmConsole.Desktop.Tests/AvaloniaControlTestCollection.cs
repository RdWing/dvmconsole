// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Desktop.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvaloniaControlTestCollection
{
    public const string Name = "Avalonia control construction";
}
