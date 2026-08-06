// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DvmConsole.Platform.Native;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for the Avalonia startup vocoder-readiness mapper.
    /// The adapter must reuse the existing native-probe contract and preserve
    /// its diagnostics without performing native loading itself.
    /// </summary>
    public sealed class VocoderReadinessTests
    {
        private static readonly string[] ExpectedExports =
        {
            "MBEEncoder_Create",
            "MBEEncoder_Encode",
            "MBEEncoder_EncodeBits",
            "MBEEncoder_Delete",
            "MBEDecoder_Create",
            "MBEDecoder_Decode",
            "MBEDecoder_DecodeBits",
            "MBEDecoder_Delete",
        };

        [Fact]
        public void LogicalLibraryName_IsLibvocoder()
        {
            Assert.Equal("libvocoder", VocoderReadiness.LogicalLibraryName);
        }

        [Fact]
        public void RequiredExports_AreExactAndOrdered()
        {
            Assert.Equal(ExpectedExports, VocoderReadiness.RequiredExports);
        }

        [Fact]
        public void NullProbe_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new VocoderReadiness(null!));
        }

        [Fact]
        public void SuccessfulProbe_ReturnsReadyResultAndUsesExactManifest()
        {
            var probe = new FakeProbe(NativeLibraryProbeResult.Success("libvocoder"));

            var result = new VocoderReadiness(probe).Check();

            Assert.True(result.IsReady);
            Assert.Equal("libvocoder", result.LogicalLibraryName);
            Assert.Null(result.Diagnostic);
            Assert.Equal("libvocoder", probe.ProbedLogicalName);
            Assert.Equal(ExpectedExports, probe.ProbedExports);
        }

        [Fact]
        public void LoadFailure_PreservesProbeDiagnosticWithoutThrowing()
        {
            const string diagnostic = "The libvocoder native library could not be loaded.";
            var probe = new FakeProbe(NativeLibraryProbeResult.Failure("libvocoder", diagnostic));

            var exception = Record.Exception(() => new VocoderReadiness(probe).Check());
            var result = new VocoderReadiness(probe).Check();

            Assert.Null(exception);
            Assert.False(result.IsReady);
            Assert.Equal("libvocoder", result.LogicalLibraryName);
            Assert.Equal(diagnostic, result.Diagnostic);
        }

        [Fact]
        public void MissingExports_PreservesProbeDiagnosticVerbatim()
        {
            const string diagnostic =
                "The libvocoder native library is missing required export(s): MBEEncoder_Encode.";
            var probe = new FakeProbe(NativeLibraryProbeResult.Failure("libvocoder", diagnostic));

            var result = new VocoderReadiness(probe).Check();

            Assert.False(result.IsReady);
            Assert.Equal(diagnostic, result.Diagnostic);
        }

        [Fact]
        public void ThrowingProbe_IsConvertedToFailureResultWithoutThrowing()
        {
            const string diagnostic = "The vocoder probe exploded deterministically.";
            var probe = new FakeProbe(
                NativeLibraryProbeResult.Success("libvocoder"),
                new InvalidOperationException(diagnostic));

            var exception = Record.Exception(() => new VocoderReadiness(probe).Check());
            var result = new VocoderReadiness(probe).Check();

            Assert.Null(exception);
            Assert.False(result.IsReady);
            Assert.Equal("libvocoder", result.LogicalLibraryName);
            Assert.Equal(diagnostic, result.Diagnostic);
        }

        [Fact]
        public void Check_InvokesProbeExactlyOnce()
        {
            var probe = new FakeProbe(NativeLibraryProbeResult.Success("libvocoder"));

            var result = new VocoderReadiness(probe).Check();

            Assert.True(result.IsReady);
            Assert.Equal(1, probe.ProbeCallCount);
        }

        [Fact]
        public void ResultShape_IsReadOnlyAndMinimal()
        {
            var resultType = typeof(VocoderReadinessResult);

            Assert.Equal(typeof(bool), resultType.GetProperty(nameof(VocoderReadinessResult.IsReady))!.PropertyType);
            Assert.Equal(typeof(string), resultType.GetProperty(nameof(VocoderReadinessResult.LogicalLibraryName))!.PropertyType);
            Assert.Equal(typeof(string), resultType.GetProperty(nameof(VocoderReadinessResult.Diagnostic))!.PropertyType);
            Assert.False(resultType.GetProperty(nameof(VocoderReadinessResult.IsReady))!.CanWrite);
            Assert.False(resultType.GetProperty(nameof(VocoderReadinessResult.LogicalLibraryName))!.CanWrite);
            Assert.False(resultType.GetProperty(nameof(VocoderReadinessResult.Diagnostic))!.CanWrite);
        }

        private sealed class FakeProbe : INativeLibraryProbe
        {
            private readonly NativeLibraryProbeResult result;
            private readonly Exception? exceptionToThrow;

            public FakeProbe(
                NativeLibraryProbeResult result,
                Exception? exceptionToThrow = null)
            {
                this.result = result;
                this.exceptionToThrow = exceptionToThrow;
            }

            public int ProbeCallCount { get; private set; }

            public string? ProbedLogicalName { get; private set; }

            public IReadOnlyList<string>? ProbedExports { get; private set; }

            public NativeLibraryProbeResult Probe(
                string logicalName,
                IReadOnlyList<string> requiredExports)
            {
                ProbeCallCount++;
                ProbedLogicalName = logicalName;
                ProbedExports = requiredExports;
                if (exceptionToThrow is not null)
                {
                    throw exceptionToThrow;
                }

                return result;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
