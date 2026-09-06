// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.IO;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class PortableFileNameTests
{
    [Theory]
    [InlineData("Dispatch")]
    [InlineData("System 1")]
    [InlineData("2026-09-04")]
    public void OrdinaryNamesRemainUnchanged(string name)
        => Assert.Equal(name, PortableFileName.Segment(name));

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("COM¹")]
    [InlineData("LPT².wav")]
    [InlineData("COM³.txt")]
    [InlineData("LPT9.wav")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Trailing. ")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j")]
    public void UnsafeNamesArePortableAndDeterministic(string name)
    {
        string segment = PortableFileName.Segment(name);
        Assert.Equal(segment, PortableFileName.Segment(name));
        Assert.NotEqual(name, segment);
        Assert.DoesNotContain(segment[^1], new[] { '.', ' ' });
        Assert.DoesNotContain(segment, character => "<>:\"/\\|?*".Contains(character));
        Assert.InRange(segment.Length, 1, 64);
    }

    [Fact]
    public void NormalizationAndTruncationDoNotCollapseDistinctInputs()
    {
        Assert.NotEqual(PortableFileName.Segment("a/b"), PortableFileName.Segment("a\\b"));
        Assert.NotEqual(PortableFileName.Segment("a/b"), PortableFileName.Segment("a_b"));
        Assert.NotEqual(PortableFileName.Segment(new string('a', 100)),
            PortableFileName.Segment(new string('a', 99) + "b"));
    }
}
