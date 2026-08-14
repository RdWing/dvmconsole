// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading.Tasks;
using fnecore;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Captures process-level managed failures into the app diagnostic sink
    /// without suppressing the runtime's existing failure behavior.
    /// </summary>
    public sealed class ApplicationDiagnostics : IDisposable
    {
        private readonly DiagnosticLogSink sink;
        private bool installed;

        public ApplicationDiagnostics(DiagnosticLogSink sink)
        {
            this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public void Install()
        {
            if (installed)
                return;

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            installed = true;
        }

        public void RecordUnhandledException(string source, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(exception);
            sink.WriteException(LogLevel.FATAL, source, exception);
        }

        public void Dispose()
        {
            if (!installed)
                return;

            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            installed = false;
        }

        private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
        {
            if (args.ExceptionObject is Exception exception)
                RecordUnhandledException("Unhandled managed exception", exception);
            else
                sink.WriteApplication(LogLevel.FATAL, "Unhandled exception object was not an Exception");
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            RecordUnhandledException("Unobserved task exception", args.Exception);
        }
    }
}