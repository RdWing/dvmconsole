// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Contract for the Gate 7.3 viewer shell and composition seams.
    /// These source pins deliberately keep UI resource ownership in the window
    /// and keep Platform independent of Core.
    /// </summary>
    public sealed class DebugLogShellWiringTests
    {
        [Fact]
        public void DebugLogWindowHasCompiledBindingsAndLifecycleActions()
        {
            string xaml = FileText("Views/DebugLogWindow.axaml");
            string codeBehind = FileText("Views/DebugLogWindow.axaml.cs");

            Assert.Contains("x:DataType=\"vm:DebugLogViewModel\"", xaml);
            Assert.Contains("Title=\"Debug Logs\"", xaml);
            Assert.Contains("Content=\"Copy\"", xaml);
            Assert.Contains("Content=\"Save\"", xaml);
            Assert.Contains("Content=\"Clear\"", xaml);
            Assert.Contains("<ScrollViewer", xaml);
            Assert.Contains("<ItemsControl", xaml);
            Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml);
            Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml);
            Assert.DoesNotContain("IsHitTestVisible=\"False\"", xaml);
            Assert.Contains("Focusable=\"False\"", xaml);
            Assert.DoesNotContain("<ListBox", xaml);
            Assert.Contains("TopLevel.GetTopLevel", codeBehind);
            Assert.Contains("SetTextAsync", codeBehind);
            Assert.Contains("FileDialogService", codeBehind);
            Assert.Contains("LogLineWritten", codeBehind);
            Assert.Contains("pendingLine", codeBehind);
            Assert.Contains("updatePosted", codeBehind);
            Assert.Contains("DrainPendingLogLine", codeBehind);
            Assert.Contains("Closed", codeBehind);
        }

        [Fact]
        public void AppAndMainWindowExposeOneDebugLogEntryPoint()
        {
            string appSource = FileText("App.axaml.cs");
            string windowSource = FileText("MainWindow.axaml.cs");

            Assert.Contains("CreateDebugLogMenuItem", appSource);
            Assert.Contains("OpenDebugLog", windowSource);
            Assert.Contains("DebugLogWindow", windowSource);
            Assert.Contains("debugLogWindow", windowSource);
            Assert.Contains("new DebugLogViewModel", windowSource);
            Assert.Contains("new DebugLogWindow", windowSource);
            Assert.Contains("debugLogWindow?.Close()", windowSource);
            Assert.Contains("fnecoreTransportFactory?.ClearDiagnosticWriter()", windowSource);
            Assert.DoesNotContain("diagnosticSink.Dispose()", windowSource);
        }

        [Fact]
        public void FneFactoryAndAudioRouterExposeDiagnosticCallbackSeams()
        {
            string factorySource = FileText("Services/FnecoreTransportFactory.cs");
            string adapterSource = FileText("Services/FnecorePeerAdapter.cs");
            string routerSource = FileTextOutsideAvalonia("DvmConsole.Platform/Audio/TalkgroupAudioRouter.cs");

            Assert.Contains("Action<LogLevel, string>", factorySource);
            Assert.Contains("Logger", adapterSource);
            Assert.Contains("Action<string>", routerSource);
            Assert.DoesNotContain("ProjectReference Include=\"..\\DvmConsole.Core", FileTextOutsideAvalonia("DvmConsole.Platform/DvmConsole.Platform.csproj"));
        }

        [Fact]
        public void DebugLogSinkCanRedactConfiguredSecrets()
        {
            const string password = "sentinel-password";
            const string key = "sentinel-preshared-key";
            var buffer = new LogBuffer();
            using var sink = new DiagnosticLogSink(buffer, new[] { password, key });

            sink.Write($"password={password}; key={key}");

            string text = string.Join("\n", buffer.GetRecentLines());
            Assert.DoesNotContain(password, text);
            Assert.DoesNotContain(key, text);
            Assert.Contains("[REDACTED]", text);
        }

        private static string FileText(string relativePath)
            => System.IO.File.ReadAllText(System.IO.Path.Combine(RepositoryRoot(), "DvmConsole.Avalonia", relativePath));

        private static string FileTextOutsideAvalonia(string relativePath)
            => System.IO.File.ReadAllText(System.IO.Path.Combine(RepositoryRoot(), relativePath));

        private static string RepositoryRoot()
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    }
}
