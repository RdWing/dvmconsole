// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using fnecore;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract for the headless Gate 7.3 diagnostic sink and viewer model.
    /// Avalonia window ownership, clipboard, file dialogs, and dispatcher
    /// marshaling remain a later shell boundary.
    /// </summary>
    public sealed class DebugLogContractTests
    {
        [Fact]
        public void Sink_WritesRenderedLinesAndPrefixesFneLevels()
        {
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer);

            sink.Write("shell started");
            sink.Write(LogLevel.WARNING, "peer is unavailable");

            Assert.Equal(
                new[] { "shell started", "WARNING peer is unavailable" },
                buffer.GetRecentLines());
        }

        [Fact]
        public void Sink_DisposeStopsLaterWritesWithoutClearingSharedBuffer()
        {
            var buffer = new LogBuffer();
            var sink = new DiagnosticLogSink(buffer);

            sink.Write("before dispose");
            sink.Dispose();
            sink.Write("after dispose");

            Assert.Equal(new[] { "before dispose" }, buffer.GetRecentLines());
        }

        [Fact]
        public void Sink_AddSensitiveValuesRedactsValuesAddedAfterConstruction()
        {
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer, new[] { "initial-secret" });

            sink.AddSensitiveValues(new[] { "reloaded-secret" });
            sink.Write("initial-secret and reloaded-secret");

            string text = string.Join("\n", buffer.GetRecentLines());
            Assert.DoesNotContain("initial-secret", text);
            Assert.DoesNotContain("reloaded-secret", text);
            Assert.Equal("[REDACTED] and [REDACTED]", text);
        }

        [Fact]
        public void Sink_PersistsRedactedLinesWithSourceAndTimestamp()
        {
            string directory = Path.Combine(Path.GetTempPath(), "dvmconsole-log-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "DvmConsole.log");
            try
            {
                var buffer = new LogBuffer();
                using var sink = new DiagnosticLogSink(buffer, new[] { "secret" }, path);

                sink.WriteApplication(LogLevel.INFO, "application started with secret");
                sink.Write(LogLevel.WARNING, "fne warning");

                string text = File.ReadAllText(path);
                Assert.Contains("[APP] INFO application started with [REDACTED]", text);
                Assert.Contains("[FNE] WARNING fne warning", text);
                Assert.DoesNotContain("secret", text);
                Assert.Contains("T", text); // ISO-8601 UTC timestamp prefix
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Sink_WriteExceptionIncludesTypeAndStackWithoutLeakingSecrets()
        {
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer, new[] { "secret" });

            sink.WriteException(
                LogLevel.ERROR,
                "background operation failed",
                new InvalidOperationException("secret"));

            string text = string.Join("\n", buffer.GetRecentLines());
            Assert.Contains("ERROR background operation failed", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("[REDACTED]", text);
            Assert.DoesNotContain("secret", text);
        }

        [Fact]
        public void ApplicationDiagnostics_RecordsManagedFailures()
        {
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer, new[] { "secret" });
            using var diagnostics = new ApplicationDiagnostics(sink);

            diagnostics.RecordUnhandledException(
                "unhandled test failure",
                new InvalidOperationException("secret"));

            string text = string.Join("\n", buffer.GetRecentLines());
            Assert.Contains("FATAL unhandled test failure", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.DoesNotContain("secret", text);
        }

        [Fact]
        public void Factory_ClearDiagnosticWriterDetachesAdapterLogger()
        {
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer);
            var factory = new FnecoreTransportFactory
            {
                DiagnosticWriter = sink.Write,
            };
            var adapter = (FnecorePeerAdapter)factory.Create(MakeSystem());

            factory.ClearDiagnosticWriter();

            FieldInfo fneField = typeof(fnecore.FneSystemBase).GetField(
                "fne",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var peer = (fnecore.FnePeer)fneField.GetValue(adapter)!;
            peer.Logger(fnecore.LogLevel.INFO, "after detach");

            Assert.Empty(buffer.GetRecentLines());
            adapter.Dispose();
        }

        [Fact]
        public void Factory_PropagatesFneDiagnosticOptionsToPeer()
        {
            var factory = new FnecoreTransportFactory
            {
                FneLogLevel = LogLevel.FATAL,
                FneRawPacketTrace = true,
                FneTrafficLogging = true,
            };
            var adapter = (FnecorePeerAdapter)factory.Create(MakeSystem());

            FieldInfo fneField = typeof(fnecore.FneSystemBase).GetField(
                "fne",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var peer = (fnecore.FnePeer)fneField.GetValue(adapter)!;

            Assert.Equal(LogLevel.FATAL, peer.LogLevel);
            Assert.True(peer.RawPacketTrace);
            Assert.True(peer.TrafficLogging);
            adapter.Dispose();
        }

        [Fact]
        public void Sink_HandlesConcurrentProducersAndKeepsTheCoreBound()
        {
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer);

            Parallel.For(0, 1000, index => sink.Write($"line-{index}"));

            var lines = buffer.GetRecentLines();
            Assert.Equal(buffer.Capacity, lines.Count);
            Assert.All(lines, line => Assert.StartsWith("line-", line));
        }

        [Fact]
        public void Viewer_SeedsAppendsBoundsAndJoinsLinesInOrder()
        {
            var buffer = new LogBuffer();
            buffer.WriteLine("one");
            buffer.WriteLine("two");
            var viewModel = new DebugLogViewModel(buffer);

            viewModel.AppendLine("three");

            Assert.Equal(new[] { "one", "two", "three" }, viewModel.Lines);
            Assert.Equal("one" + Environment.NewLine + "two" + Environment.NewLine + "three", viewModel.GetTextSnapshot());

            foreach (int index in Enumerable.Range(0, buffer.Capacity + 1))
                viewModel.AppendLine($"viewer-{index}");

            Assert.Equal(buffer.Capacity, viewModel.Lines.Count);
            Assert.Equal("viewer-1", viewModel.Lines[0]);
            Assert.Equal("viewer-500", viewModel.Lines[^1]);
        }

        [Fact]
        public void Viewer_ClearOnlyClearsItsVisibleSnapshot()
        {
            var buffer = new LogBuffer();
            buffer.WriteLine("shared line");
            var viewModel = new DebugLogViewModel(buffer);

            viewModel.Clear();

            Assert.Empty(viewModel.Lines);
            Assert.Equal(new[] { "shared line" }, buffer.GetRecentLines());
            Assert.Equal(string.Empty, viewModel.GetTextSnapshot());
        }

        private static dvmconsole.Codeplug.System MakeSystem()
            => new()
            {
                Name = "Debug Log Test",
                Identity = "Console 1",
                Address = "127.0.0.1",
                Port = 62031,
                PeerId = 1000001,
                Password = "pw",
                Encrypted = false,
            };
    }
}
