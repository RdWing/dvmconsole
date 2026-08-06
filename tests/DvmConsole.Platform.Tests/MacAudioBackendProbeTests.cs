// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the no-Mac-safe MacAudioBackendProbe slice of
* DvmConsole.Platform.Audio.Mac. These facts are written entirely against the
* approved design: MacAudioBackendProbe is safe to construct and probe on any
* host — non-macOS hosts receive NotApplicable with a null diagnostic and no
* framework results, and the native probe is never invoked at all. On macOS
* the verdict is Available or Unavailable, built from exactly two framework
* probes (CoreAudio first, then AudioToolbox) driven by public, exact export
* manifests, where every framework is still probed even after a failure and
* failures are carried as results — never exceptions — with diagnostics that
* name the framework and its missing exports. Logical framework names never
* carry OS file names, extensions or paths.
*
* GREEN contract gate: the locked types are implemented without direct native
* calls on non-macOS hosts; actual CoreAudio behavior remains an Apple-host
* verification gate.
*/
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Native;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Contract gate for the no-Mac-safe <c>MacAudioBackendProbe</c> and its
    /// result model, written against the approved design.
    /// </summary>
    public sealed class MacAudioBackendProbeTests
    {
        /* The approved export manifests, hard-coded so the probe's public
           manifests cannot drift from the contract. */

        private static readonly string[] ExpectedCoreAudioExports =
        {
            "AudioObjectGetPropertyDataSize",
            "AudioObjectGetPropertyData",
            "AudioObjectAddPropertyListener",
            "AudioObjectRemovePropertyListener",
        };

        private static readonly string[] ExpectedAudioToolboxExports =
        {
            "AudioQueueNewInput",
            "AudioQueueNewOutput",
            "AudioQueueStart",
            "AudioQueueStop",
            "AudioQueueDispose",
            "AudioQueueAllocateBuffer",
            "AudioQueueEnqueueBuffer",
            "AudioFileOpenURL",
            "AudioQueueSetParameter",
            "AudioQueueSetProperty",
        };

        /*
        ** Status model
        */

        /// <summary>
        /// The status model is exactly the three approved members — no extra
        /// states, all distinct.
        /// </summary>
        [Fact]
        public void Status_DefinesExactlyTheThreeApprovedDistinctMembers()
        {
            Assert.True(Enum.IsDefined(typeof(MacAudioBackendStatus), MacAudioBackendStatus.NotApplicable));
            Assert.True(Enum.IsDefined(typeof(MacAudioBackendStatus), MacAudioBackendStatus.Available));
            Assert.True(Enum.IsDefined(typeof(MacAudioBackendStatus), MacAudioBackendStatus.Unavailable));

            Assert.Equal(3, Enum.GetValues(typeof(MacAudioBackendStatus)).Length);

            Assert.NotEqual(MacAudioBackendStatus.NotApplicable, MacAudioBackendStatus.Available);
            Assert.NotEqual(MacAudioBackendStatus.NotApplicable, MacAudioBackendStatus.Unavailable);
            Assert.NotEqual(MacAudioBackendStatus.Available, MacAudioBackendStatus.Unavailable);
        }

        /*
        ** Result model
        */

        /// <summary>
        /// The NotApplicable factory carries the NotApplicable status, a null
        /// diagnostic and an empty (never null) framework result list.
        /// </summary>
        [Fact]
        public void NotApplicable_Result_CarriesStatusNullDiagnosticAndEmptyResults()
        {
            var result = MacAudioBackendProbeResult.NotApplicable();

            Assert.Equal(MacAudioBackendStatus.NotApplicable, result.Status);
            Assert.Null(result.Diagnostic);
            Assert.Empty(result.FrameworkResults);
        }

        /// <summary>
        /// The Available factory carries the Available status, a null
        /// diagnostic and the exact framework results it was given.
        /// </summary>
        [Fact]
        public void Available_Result_CarriesStatusNullDiagnosticAndFrameworkResults()
        {
            var frameworkResults = new[]
            {
                NativeLibraryProbeResult.Success(MacAudioBackendProbe.CoreAudioFrameworkName),
                NativeLibraryProbeResult.Success(MacAudioBackendProbe.AudioToolboxFrameworkName),
            };

            var result = MacAudioBackendProbeResult.Available(frameworkResults);

            Assert.Equal(MacAudioBackendStatus.Available, result.Status);
            Assert.Null(result.Diagnostic);
            Assert.Equal(frameworkResults, result.FrameworkResults);
        }

        /// <summary>
        /// The Unavailable factory carries the Unavailable status, the exact
        /// diagnostic it was given and the framework results.
        /// </summary>
        [Fact]
        public void Unavailable_Result_CarriesStatusDiagnosticAndFrameworkResults()
        {
            const string diagnostic = "macOS audio backend unavailable.";
            var frameworkResults = new[]
            {
                NativeLibraryProbeResult.Failure(
                    MacAudioBackendProbe.CoreAudioFrameworkName,
                    "The CoreAudio native library is missing required export(s): AudioObjectGetPropertyData."),
            };

            var result = MacAudioBackendProbeResult.Unavailable(diagnostic, frameworkResults);

            Assert.Equal(MacAudioBackendStatus.Unavailable, result.Status);
            Assert.Equal(diagnostic, result.Diagnostic);
            Assert.Equal(frameworkResults, result.FrameworkResults);
        }

        /// <summary>
        /// Result factories defensively copy caller-owned framework result
        /// lists, so later source-list mutation cannot alter the outcome.
        /// </summary>
        [Fact]
        public void ResultFactories_DefensivelyCopyFrameworkResults()
        {
            var source = new List<NativeLibraryProbeResult>
            {
                NativeLibraryProbeResult.Success(MacAudioBackendProbe.CoreAudioFrameworkName),
            };

            var available = MacAudioBackendProbeResult.Available(source);
            var unavailable = MacAudioBackendProbeResult.Unavailable("diagnostic", source);

            source[0] = NativeLibraryProbeResult.Failure(
                MacAudioBackendProbe.CoreAudioFrameworkName,
                "changed after factory call");

            Assert.True(available.FrameworkResults[0].IsSuccess);
            Assert.True(unavailable.FrameworkResults[0].IsSuccess);
        }

        /*
        ** Constructor guards
        */

        /// <summary>
        /// A null native probe is a programming error on both constructors.
        /// </summary>
        [Fact]
        public void Constructor_NullNativeProbe_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new MacAudioBackendProbe(null!));
            Assert.Throws<ArgumentNullException>(() => new MacAudioBackendProbe(null!, () => true));
        }

        /// <summary>
        /// A null host predicate is a programming error.
        /// </summary>
        [Fact]
        public void Constructor_NullIsMacOS_ThrowsArgumentNullException()
        {
            var fake = new RecordingNativeLibraryProbe();

            Assert.Throws<ArgumentNullException>(() => new MacAudioBackendProbe(fake, null!));
        }

        /// <summary>
        /// The single-argument constructor derives the host check from the
        /// runtime and must return a verdict on any host without throwing.
        /// </summary>
        [Fact]
        public void Constructor_WithDefaultIsMacOS_ProbeReturnsAVerdictWithoutThrowing()
        {
            var probe = new MacAudioBackendProbe(new RecordingNativeLibraryProbe());

            var result = probe.Probe();

            Assert.NotNull(result);
        }

        /*
        ** Non-macOS short circuit
        */

        /// <summary>
        /// A non-macOS host receives NotApplicable with a null diagnostic and
        /// an empty framework result list — and the native probe is never
        /// invoked at all.
        /// </summary>
        [Fact]
        public void Probe_NonMac_ReturnsNotApplicable_WithoutInvokingTheNativeProbe()
        {
            var probe = new MacAudioBackendProbe(
                new ThrowIfCalledNativeLibraryProbe(),
                () => false);

            var result = probe.Probe();

            Assert.Equal(MacAudioBackendStatus.NotApplicable, result.Status);
            Assert.Null(result.Diagnostic);
            Assert.Empty(result.FrameworkResults);
        }

        /// <summary>
        /// Repeated probing on a non-macOS host is stable: every call yields
        /// the same NotApplicable verdict and the native probe stays
        /// untouched.
        /// </summary>
        [Fact]
        public void Probe_NonMac_RepeatedCalls_AreStableAndNeverInvokeTheNativeProbe()
        {
            var probe = new MacAudioBackendProbe(
                new ThrowIfCalledNativeLibraryProbe(),
                () => false);

            for (var i = 0; i < 3; i++)
            {
                var result = probe.Probe();

                Assert.Equal(MacAudioBackendStatus.NotApplicable, result.Status);
                Assert.Null(result.Diagnostic);
                Assert.Empty(result.FrameworkResults);
            }
        }

        /*
        ** Framework names and export manifests
        */

        /// <summary>
        /// Framework names are the approved logical names, never OS file
        /// names: no extensions and no paths.
        /// </summary>
        [Fact]
        public void FrameworkNames_AreExactLogicalNames_WithoutOsFileNameTrappings()
        {
            Assert.Equal("CoreAudio", MacAudioBackendProbe.CoreAudioFrameworkName);
            Assert.Equal("AudioToolbox", MacAudioBackendProbe.AudioToolboxFrameworkName);

            foreach (var name in new[]
            {
                MacAudioBackendProbe.CoreAudioFrameworkName,
                MacAudioBackendProbe.AudioToolboxFrameworkName,
            })
            {
                Assert.DoesNotContain(".dylib", name);
                Assert.DoesNotContain(".framework", name);
                Assert.DoesNotContain(".dll", name);
                Assert.DoesNotContain(".so", name);
                Assert.DoesNotContain("/", name);
            }
        }

        /// <summary>
        /// The CoreAudio export manifest is exactly the four approved exports
        /// in order, with no OS file name trappings.
        /// </summary>
        [Fact]
        public void CoreAudioExports_AreExactlyTheApprovedExportsInOrder()
        {
            Assert.Equal(ExpectedCoreAudioExports, MacAudioBackendProbe.CoreAudioExports);

            foreach (var export in MacAudioBackendProbe.CoreAudioExports)
            {
                Assert.DoesNotContain(".dylib", export);
                Assert.DoesNotContain(".framework", export);
                Assert.DoesNotContain(".dll", export);
                Assert.DoesNotContain(".so", export);
                Assert.DoesNotContain("/", export);
            }
        }

        /// <summary>
        /// The AudioToolbox export manifest is exactly the ten approved
        /// exports in order, with no OS file name trappings.
        /// </summary>
        [Fact]
        public void AudioToolboxExports_AreExactlyTheApprovedExportsInOrder()
        {
            Assert.Equal(ExpectedAudioToolboxExports, MacAudioBackendProbe.AudioToolboxExports);

            foreach (var export in MacAudioBackendProbe.AudioToolboxExports)
            {
                Assert.DoesNotContain(".dylib", export);
                Assert.DoesNotContain(".framework", export);
                Assert.DoesNotContain(".dll", export);
                Assert.DoesNotContain(".so", export);
                Assert.DoesNotContain("/", export);
            }
        }

        /*
        ** macOS success path
        */

        /// <summary>
        /// On macOS with every framework available the verdict is Available
        /// with a null diagnostic and exactly two ordered framework results —
        /// CoreAudio first, then AudioToolbox — each probed under its exact
        /// logical name and exact export manifest.
        /// </summary>
        [Fact]
        public void Probe_MacAllFrameworksAvailable_ReturnsAvailable_WithTwoOrderedResults()
        {
            var fake = new RecordingNativeLibraryProbe();
            var probe = new MacAudioBackendProbe(fake, () => true);

            var result = probe.Probe();

            Assert.Equal(MacAudioBackendStatus.Available, result.Status);
            Assert.Null(result.Diagnostic);

            Assert.Equal(2, result.FrameworkResults.Count);
            Assert.Equal(MacAudioBackendProbe.CoreAudioFrameworkName, result.FrameworkResults[0].LogicalName);
            Assert.True(result.FrameworkResults[0].IsSuccess);
            Assert.Null(result.FrameworkResults[0].Diagnostic);
            Assert.Equal(MacAudioBackendProbe.AudioToolboxFrameworkName, result.FrameworkResults[1].LogicalName);
            Assert.True(result.FrameworkResults[1].IsSuccess);
            Assert.Null(result.FrameworkResults[1].Diagnostic);

            Assert.Equal(2, fake.Calls.Count);
            Assert.Equal(MacAudioBackendProbe.CoreAudioFrameworkName, fake.Calls[0].LogicalName);
            Assert.Equal(ExpectedCoreAudioExports, fake.Calls[0].Exports);
            Assert.Equal(MacAudioBackendProbe.AudioToolboxFrameworkName, fake.Calls[1].LogicalName);
            Assert.Equal(ExpectedAudioToolboxExports, fake.Calls[1].Exports);
        }

        /*
        ** macOS failure paths
        */

        /// <summary>
        /// A failed CoreAudio probe yields Unavailable: the diagnostic names
        /// the framework and the missing export, AudioToolbox is still
        /// probed, and the failure is a result — Probe() does not throw.
        /// </summary>
        [Fact]
        public void Probe_MacCoreAudioFailure_ReturnsUnavailable_NamingFrameworkAndMissingExport_StillProbesAudioToolbox()
        {
            const string missingExport = "AudioObjectGetPropertyData";
            var fake = new RecordingNativeLibraryProbe(
                (MacAudioBackendProbe.CoreAudioFrameworkName,
                 FrameworkFailure(MacAudioBackendProbe.CoreAudioFrameworkName, missingExport)));
            var probe = new MacAudioBackendProbe(fake, () => true);

            var result = probe.Probe();

            Assert.Equal(MacAudioBackendStatus.Unavailable, result.Status);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains(MacAudioBackendProbe.CoreAudioFrameworkName, result.Diagnostic);
            Assert.Contains(missingExport, result.Diagnostic);

            Assert.Equal(2, result.FrameworkResults.Count);
            Assert.False(result.FrameworkResults[0].IsSuccess);
            Assert.Contains(missingExport, result.FrameworkResults[0].Diagnostic);
            Assert.True(result.FrameworkResults[1].IsSuccess);

            Assert.Equal(2, fake.Calls.Count);
            Assert.Equal(MacAudioBackendProbe.AudioToolboxFrameworkName, fake.Calls[1].LogicalName);
        }

        /// <summary>
        /// A failed AudioToolbox probe yields Unavailable naming the framework
        /// and its missing export; the CoreAudio result is still present and
        /// successful.
        /// </summary>
        [Fact]
        public void Probe_MacAudioToolboxFailure_ReturnsUnavailable_NamingFrameworkAndMissingExport()
        {
            const string missingExport = "AudioQueueStart";
            var fake = new RecordingNativeLibraryProbe(
                (MacAudioBackendProbe.AudioToolboxFrameworkName,
                 FrameworkFailure(MacAudioBackendProbe.AudioToolboxFrameworkName, missingExport)));
            var probe = new MacAudioBackendProbe(fake, () => true);

            var result = probe.Probe();

            Assert.Equal(MacAudioBackendStatus.Unavailable, result.Status);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains(MacAudioBackendProbe.AudioToolboxFrameworkName, result.Diagnostic);
            Assert.Contains(missingExport, result.Diagnostic);

            Assert.Equal(2, result.FrameworkResults.Count);
            Assert.True(result.FrameworkResults[0].IsSuccess);
            Assert.False(result.FrameworkResults[1].IsSuccess);
            Assert.Contains(missingExport, result.FrameworkResults[1].Diagnostic);

            Assert.Equal(2, fake.Calls.Count);
        }

        /// <summary>
        /// When both frameworks fail the diagnostic aggregates both failures
        /// — both framework names and both missing export sets appear — and
        /// both frameworks were still probed.
        /// </summary>
        [Fact]
        public void Probe_MacBothFrameworksFail_ReturnsUnavailable_AggregatingBothFailures()
        {
            const string coreAudioMissingExport = "AudioObjectGetPropertyDataSize";
            const string audioToolboxMissingExport = "AudioFileOpenURL";
            var fake = new RecordingNativeLibraryProbe(
                (MacAudioBackendProbe.CoreAudioFrameworkName,
                 FrameworkFailure(MacAudioBackendProbe.CoreAudioFrameworkName, coreAudioMissingExport)),
                (MacAudioBackendProbe.AudioToolboxFrameworkName,
                 FrameworkFailure(MacAudioBackendProbe.AudioToolboxFrameworkName, audioToolboxMissingExport)));
            var probe = new MacAudioBackendProbe(fake, () => true);

            var result = probe.Probe();

            Assert.Equal(MacAudioBackendStatus.Unavailable, result.Status);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains(MacAudioBackendProbe.CoreAudioFrameworkName, result.Diagnostic);
            Assert.Contains(coreAudioMissingExport, result.Diagnostic);
            Assert.Contains(MacAudioBackendProbe.AudioToolboxFrameworkName, result.Diagnostic);
            Assert.Contains(audioToolboxMissingExport, result.Diagnostic);

            Assert.Equal(2, result.FrameworkResults.Count);
            Assert.False(result.FrameworkResults[0].IsSuccess);
            Assert.False(result.FrameworkResults[1].IsSuccess);

            Assert.Equal(2, fake.Calls.Count);
        }

        /// <summary>
        /// Framework probe failures are reported through the result model,
        /// never through exceptions: Probe() returns an Unavailable verdict
        /// when the underlying probe returns failures.
        /// </summary>
        [Fact]
        public void Probe_MacFailingFrameworks_AreResults_NotExceptions()
        {
            var fake = new RecordingNativeLibraryProbe(
                (MacAudioBackendProbe.CoreAudioFrameworkName,
                 FrameworkFailure(MacAudioBackendProbe.CoreAudioFrameworkName, "AudioObjectGetPropertyData")));
            var probe = new MacAudioBackendProbe(fake, () => true);

            var result = probe.Probe();

            Assert.Equal(MacAudioBackendStatus.Unavailable, result.Status);
            Assert.NotNull(result.Diagnostic);
        }

        /*
        ** Fakes and helpers
        */

        /// <summary>
        /// Live recording fake: implements <see cref="INativeLibraryProbe"/>,
        /// records every call (logical name plus the exact export list) in
        /// order, and returns a configurable per-framework result, defaulting
        /// to success for any framework not overridden.
        /// </summary>
        private sealed class RecordingNativeLibraryProbe : INativeLibraryProbe
        {
            private readonly IReadOnlyDictionary<string, NativeLibraryProbeResult> _overrides;

            public RecordingNativeLibraryProbe(
                params (string LogicalName, NativeLibraryProbeResult Result)[] overrides)
            {
                _overrides = overrides.ToDictionary(o => o.LogicalName, o => o.Result);
            }

            /// <summary>Every probe call in invocation order.</summary>
            public List<(string LogicalName, IReadOnlyList<string> Exports)> Calls { get; } = new();

            public NativeLibraryProbeResult Probe(string logicalName, IReadOnlyList<string> requiredExports)
            {
                Calls.Add((logicalName, requiredExports));

                return _overrides.TryGetValue(logicalName, out var result)
                    ? result
                    : NativeLibraryProbeResult.Success(logicalName);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>
        /// Fake that fails the test if the probe ever invokes it: proves the
        /// non-macOS path short-circuits before touching the native probe.
        /// </summary>
        private sealed class ThrowIfCalledNativeLibraryProbe : INativeLibraryProbe
        {
            public NativeLibraryProbeResult Probe(string logicalName, IReadOnlyList<string> requiredExports)
            {
                throw new InvalidOperationException(
                    $"The native probe must never be invoked on a non-macOS host (attempted '{logicalName}').");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>
        /// Failure result in the production probe's diagnostic shape: names
        /// the framework and a single missing export.
        /// </summary>
        private static NativeLibraryProbeResult FrameworkFailure(string frameworkName, string missingExport)
        {
            return NativeLibraryProbeResult.Failure(
                frameworkName,
                $"The {frameworkName} native library is missing required export(s): {missingExport}.");
        }
    }
}
