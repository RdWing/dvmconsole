// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Storage.Tests;

public sealed class CallHistoryCsvTests
{
    [Fact]
    public void PreservesQuotedMultilineFieldsAndLeavesHostDocumentOpen()
    {
        using var output = new MemoryStream();
        CallHistoryCsv.Write(output, [new(DateTimeOffset.UnixEpoch, null, TimeSpan.FromMilliseconds(1250),
            "North, dispatch", "A\"B", "1", "Unit\nOne", "2", "P25", "Clear", 123)], leaveOpen: true);
        Assert.True(output.CanWrite);
        var bytes = output.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("Start,End,DurationSeconds,System,Channel,SourceId,Caller,Talkgroup,Protocol,Encryption,StreamId", text);
        Assert.Contains("\"1.25\",\"North, dispatch\",\"A\"\"B\",\"1\",\"Unit\nOne\"", text);
        Assert.Contains(",123", text);
    }
}
