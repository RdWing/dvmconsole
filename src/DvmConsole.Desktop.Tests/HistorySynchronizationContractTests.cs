// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HistorySynchronizationContractTests
{
    [Fact]
    public void InitialPopulationNeverCallsValueEqualityAndKeepsRowIdentity()
    {
        var rows = Enumerable.Range(0, 20_000).Select(_ => new EqualRow()).ToArray();
        var target = new ObservableCollection<EqualRow>();
        EqualRow.Comparisons = 0;
        HistoryViewSynchronizer.Synchronize(target, rows);
        Assert.Equal(0, EqualRow.Comparisons);
        Assert.Equal(rows.Length, target.Count);
        Assert.Same(rows[^1], target[^1]);
    }

    [Fact]
    public void ReorderingUsesReferenceIdentityEvenWhenRowsCompareEqual()
    {
        EqualRow first = new(), second = new(), added = new();
        var target = new ObservableCollection<EqualRow> { first, second };
        HistoryViewSynchronizer.Synchronize(target, [second, added, first]);
        Assert.Same(second, target[0]);
        Assert.Same(added, target[1]);
        Assert.Same(first, target[2]);
    }

    private sealed class EqualRow
    {
        public static int Comparisons;
        public override bool Equals(object? obj) { Comparisons++; return obj is EqualRow; }
        public override int GetHashCode() => 0;
    }
}
