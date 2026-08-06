// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using dvmconsole;

namespace DvmConsole.Core.Configuration
{
    /// <summary>
    /// Typed, non-throwing outcome of a codeplug load. Exactly one of
    /// three outcomes is represented: success (<see cref="Succeeded"/>
    /// with a parsed <see cref="Codeplug"/>), failure (Succeeded false
    /// with an <see cref="ErrorMessage"/>), or a missing file
    /// (<see cref="FileMissing"/>). Results are constructed only by
    /// <see cref="CodeplugLoader"/>; callers read the properties.
    /// </summary>
    public sealed class CodeplugLoadResult
    {
        /// <summary>
        /// True when the codeplug text parsed and normalized successfully.
        /// </summary>
        public bool Succeeded { get; }

        /// <summary>
        /// The parsed and normalized codeplug on success; null otherwise.
        /// </summary>
        public Codeplug? Codeplug { get; }

        /// <summary>
        /// Human-readable failure diagnostic; null on success.
        /// </summary>
        public string? ErrorMessage { get; }

        /// <summary>
        /// True when no file could be read because the path was null or
        /// blank, or the file does not exist.
        /// </summary>
        public bool FileMissing { get; }

        /// <summary>
        /// Creates a result. Only <see cref="CodeplugLoader"/> constructs
        /// results.
        /// </summary>
        internal CodeplugLoadResult(bool succeeded, Codeplug? codeplug, string? errorMessage, bool fileMissing)
        {
            Succeeded = succeeded;
            Codeplug = codeplug;
            ErrorMessage = errorMessage;
            FileMissing = fileMissing;
        }

        /// <summary>
        /// Creates a successful result carrying the parsed codeplug.
        /// </summary>
        internal static CodeplugLoadResult Success(Codeplug? codeplug)
            => new CodeplugLoadResult(true, codeplug, null, false);

        /// <summary>
        /// Creates a failed result carrying the given diagnostic.
        /// </summary>
        internal static CodeplugLoadResult Failed(string errorMessage)
            => new CodeplugLoadResult(false, null, errorMessage, false);

        /// <summary>
        /// Creates a failed result for a missing file, carrying the given
        /// path in its diagnostic when one was supplied.
        /// </summary>
        internal static CodeplugLoadResult NotFound(string? path = null)
            => new CodeplugLoadResult(false, null, "codeplug file not found: " + (path ?? "<none>"), true);
    }
}
