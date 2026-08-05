// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using DvmConsole.Platform.Native;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// The availability verdict of the macOS (CoreAudio/AudioToolbox) audio
    /// backend on the host. Exactly the three approved members.
    /// </summary>
    public enum MacAudioBackendStatus
    {
        /// <summary>
        /// The host is not macOS, so the backend does not apply and was not
        /// probed at all.
        /// </summary>
        NotApplicable,

        /// <summary>
        /// Every required macOS audio framework probed successfully.
        /// </summary>
        Available,

        /// <summary>
        /// At least one required macOS audio framework is missing or lacks
        /// required exports.
        /// </summary>
        Unavailable,
    }

    /// <summary>
    /// Immutable outcome of a <see cref="MacAudioBackendProbe"/> probe:
    /// a verdict, an optional diagnostic naming failed frameworks, and the
    /// ordered per-framework probe results. Result lists are defensively
    /// copied on construction, so later mutation of the supplied list cannot
    /// change this result.
    /// </summary>
    public sealed class MacAudioBackendProbeResult
    {
        private MacAudioBackendProbeResult(
            MacAudioBackendStatus status,
            string? diagnostic,
            IReadOnlyList<NativeLibraryProbeResult> frameworkResults)
        {
            Status = status;
            Diagnostic = diagnostic;
            FrameworkResults = frameworkResults;
        }

        /// <summary>The availability verdict.</summary>
        public MacAudioBackendStatus Status { get; }

        /// <summary>
        /// Human-readable diagnostic naming every failed framework and its
        /// supplied diagnostic on Unavailable; null otherwise.
        /// </summary>
        public string? Diagnostic { get; }

        /// <summary>
        /// The ordered per-framework probe results (CoreAudio first, then
        /// AudioToolbox on macOS). Never null; empty on NotApplicable.
        /// </summary>
        public IReadOnlyList<NativeLibraryProbeResult> FrameworkResults { get; }

        /// <summary>
        /// The non-macOS outcome: NotApplicable status, null diagnostic and an
        /// empty (never null) framework result list.
        /// </summary>
        public static MacAudioBackendProbeResult NotApplicable()
            => new(MacAudioBackendStatus.NotApplicable, null, Array.Empty<NativeLibraryProbeResult>());

        /// <summary>
        /// The fully-available outcome: Available status, null diagnostic and
        /// the exact framework results supplied, preserved in order.
        /// </summary>
        /// <param name="frameworkResults">Ordered per-framework probe results;
        /// copied defensively.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="frameworkResults"/> is null.</exception>
        public static MacAudioBackendProbeResult Available(
            IReadOnlyList<NativeLibraryProbeResult> frameworkResults)
        {
            return new(MacAudioBackendStatus.Available, null, CopyResults(frameworkResults));
        }

        /// <summary>
        /// The degraded outcome: Unavailable status with the supplied
        /// diagnostic and framework results.
        /// </summary>
        /// <param name="diagnostic">Diagnostic naming the failed frameworks
        /// and their supplied diagnostics.</param>
        /// <param name="frameworkResults">Ordered per-framework probe results;
        /// copied defensively.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="frameworkResults"/> is null.</exception>
        public static MacAudioBackendProbeResult Unavailable(
            string diagnostic,
            IReadOnlyList<NativeLibraryProbeResult> frameworkResults)
        {
            if (diagnostic == null)
                throw new ArgumentNullException(nameof(diagnostic));

            return new(MacAudioBackendStatus.Unavailable, diagnostic, CopyResults(frameworkResults));
        }

        /*
        ** Helpers
        */

        /// <summary>
        /// Snapshots the supplied results into a fresh array so the result
        /// model stays immutable regardless of what the caller does with its
        /// list afterwards.
        /// </summary>
        private static IReadOnlyList<NativeLibraryProbeResult> CopyResults(
            IReadOnlyList<NativeLibraryProbeResult> frameworkResults)
        {
            if (frameworkResults == null)
                throw new ArgumentNullException(nameof(frameworkResults));

            var copy = new NativeLibraryProbeResult[frameworkResults.Count];
            for (var i = 0; i < frameworkResults.Count; i++)
            {
                copy[i] = frameworkResults[i];
            }

            return copy;
        }
    }

    /// <summary>
    /// No-Mac-safe probe for the macOS audio backend. Safe to construct and
    /// probe on any host: on non-macOS hosts the verdict is
    /// <see cref="MacAudioBackendStatus.NotApplicable"/> with a null
    /// diagnostic and no framework results, and the native probe is never
    /// invoked at all. On macOS exactly two frameworks are probed in order —
    /// CoreAudio, then AudioToolbox — under their exact logical names and
    /// exact public export manifests. Every framework is still probed even
    /// after a failure, and framework failures are carried as results — never
    /// thrown — with an aggregated diagnostic naming every failed framework
    /// and its supplied diagnostic. The injected native probe remains owned
    /// by the caller; this wrapper does not dispose it.
    /// </summary>
    public sealed class MacAudioBackendProbe
    {
        /*
        ** Constants
        */

        /// <summary>
        /// The logical CoreAudio framework name, without OS file name
        /// trappings (no extension, no path).
        /// </summary>
        public const string CoreAudioFrameworkName = "CoreAudio";

        /// <summary>
        /// The logical AudioToolbox framework name, without OS file name
        /// trappings (no extension, no path).
        /// </summary>
        public const string AudioToolboxFrameworkName = "AudioToolbox";

        /// <summary>
        /// The exact, ordered CoreAudio export manifest the backend requires.
        /// </summary>
        public static readonly IReadOnlyList<string> CoreAudioExports =
            Array.AsReadOnly(new[]
            {
                "AudioObjectGetPropertyDataSize",
                "AudioObjectGetPropertyData",
            });

        /// <summary>
        /// The exact, ordered AudioToolbox export manifest the backend
        /// requires.
        /// </summary>
        public static readonly IReadOnlyList<string> AudioToolboxExports =
            Array.AsReadOnly(new[]
            {
                "AudioQueueNewInput",
                "AudioQueueNewOutput",
                "AudioQueueStart",
                "AudioQueueStop",
                "AudioQueueDispose",
                "AudioQueueAllocateBuffer",
                "AudioQueueEnqueueBuffer",
                "AudioFileOpenURL",
            });

        /*
        ** Fields
        */

        private readonly INativeLibraryProbe _nativeProbe;
        private readonly Func<bool> _isMacOS;

        /*
        ** Constructors
        */

        /// <summary>
        /// Derives the host check from the runtime (<see cref="PlatformInfo.IsMacOS"/>),
        /// so probing returns a verdict on any host without throwing.
        /// </summary>
        /// <param name="nativeProbe">The native library probe driving the
        /// framework checks; never invoked on non-macOS hosts.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="nativeProbe"/> is null.</exception>
        public MacAudioBackendProbe(INativeLibraryProbe nativeProbe)
            : this(nativeProbe, () => PlatformInfo.IsMacOS)
        {
        }

        /// <summary>
        /// Uses the supplied host predicate, so the macOS check can be
        /// controlled (e.g. in tests).
        /// </summary>
        /// <param name="nativeProbe">The native library probe driving the
        /// framework checks; never invoked when the predicate reports a
        /// non-macOS host.</param>
        /// <param name="isMacOS">Host predicate returning true on macOS.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="nativeProbe"/> or <paramref name="isMacOS"/> is
        /// null.</exception>
        public MacAudioBackendProbe(INativeLibraryProbe nativeProbe, Func<bool> isMacOS)
        {
            _nativeProbe = nativeProbe ?? throw new ArgumentNullException(nameof(nativeProbe));
            _isMacOS = isMacOS ?? throw new ArgumentNullException(nameof(isMacOS));
        }

        /*
        ** Methods
        */

        /// <summary>
        /// Probes the macOS audio backend and returns an immutable verdict.
        /// Short-circuits to NotApplicable on non-macOS hosts without ever
        /// invoking the native probe; on macOS probes CoreAudio and then
        /// AudioToolbox, always probing both even after a failure, and reports
        /// failures as results — never exceptions.
        /// </summary>
        public MacAudioBackendProbeResult Probe()
        {
            if (!_isMacOS())
            {
                return MacAudioBackendProbeResult.NotApplicable();
            }

            // Both frameworks are always probed, in order, even when the
            // first fails: the full picture matters for diagnostics.
            var frameworkResults = new List<NativeLibraryProbeResult>(2)
            {
                _nativeProbe.Probe(CoreAudioFrameworkName, CoreAudioExports),
                _nativeProbe.Probe(AudioToolboxFrameworkName, AudioToolboxExports),
            };

            if (frameworkResults.All(result => result.IsSuccess))
            {
                return MacAudioBackendProbeResult.Available(frameworkResults);
            }

            return MacAudioBackendProbeResult.Unavailable(
                BuildUnavailableDiagnostic(frameworkResults),
                frameworkResults);
        }

        /*
        ** Helpers
        */

        /// <summary>
        /// Aggregates every failed framework's supplied diagnostic into a
        /// single message, so the verdict names all failed frameworks and
        /// their missing exports.
        /// </summary>
        private static string BuildUnavailableDiagnostic(
            IReadOnlyList<NativeLibraryProbeResult> frameworkResults)
        {
            var failures = frameworkResults
                .Where(result => !result.IsSuccess)
                .Select(result =>
                    result.LogicalName + ": " +
                    (result.Diagnostic ?? "No diagnostic was supplied."));

            return "The macOS audio backend is unavailable: " + string.Join("; ", failures);
        }
    }
}
