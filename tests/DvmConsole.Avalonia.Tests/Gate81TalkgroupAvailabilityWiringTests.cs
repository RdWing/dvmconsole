// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class Gate81TalkgroupAvailabilityWiringTests
    {
        [Fact]
        public void ShellUsesConnectionAvailabilityBeforeEveryTransmitEntryPoint()
        {
            var source = File.ReadAllText(SourcePath("MainWindow.axaml.cs"));

            Assert.True(Count(source, "IsTransmitTargetAvailable") >= 5);
            Assert.Contains("IsFneSystemAvailable(target.SystemName)", source);
            Assert.DoesNotContain("IFneTalkgroupStatusProvider provider", source);
            Assert.DoesNotContain("QueryTalkgroupAvailability(query)", source);
            Assert.DoesNotContain("Target TG unavailable on FNE", source);
            Assert.Contains("targets.All(IsTransmitTargetAvailable)", source);
            Assert.Contains("target => IsTransmitTargetAvailable(target)", source);
        }

        [Fact]
        public void CardPttPointerDoesNotAlsoSelectTheCard()
        {
            var source = File.ReadAllText(SourcePath("MainWindow.axaml.cs"));

            Assert.Contains(
                "sender is not Border card || e.Source is Button",
                source);
        }

        [Fact]
        public void FnecoreAdapterMapsAnnouncedRulesThroughCoreProvider()
        {
            var adapter = File.ReadAllText(
                Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", "Services", "FnecorePeerAdapter.cs"));
            var core = File.ReadAllText(
                Path.Combine(RepositoryRoot(), "DvmConsole.Core", "Networking", "TalkgroupAvailability.cs"));

            Assert.Contains("IFneTalkgroupStatusProvider", adapter);
            Assert.Contains("fne.AnnouncedTGs", adapter);
            Assert.Contains("TalkgroupAvailabilityEvaluator.Evaluate", adapter);
            Assert.Contains("public interface IFneTalkgroupStatusProvider", core);
        }

        private static int Count(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static string RepositoryRoot()
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

        private static string SourcePath(string fileName)
            => Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", fileName);
    }
}
