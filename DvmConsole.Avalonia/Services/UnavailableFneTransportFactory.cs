// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;
using DvmConsole.Core.Networking;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Dependency-free fallback <see cref="IFneTransportFactory"/> used
    /// by the shell composition until the real fnecore-backed transport
    /// adapter exists (a later slice). Creating a transport always fails
    /// with <see cref="PlatformNotSupportedException"/>; with zero
    /// configured systems the factory is never invoked, so the FNE
    /// slice stays inert and honest. Mirrors the
    /// <c>UnavailableGlobalHotkeyService</c> pattern.
    /// </summary>
    public sealed class UnavailableFneTransportFactory : IFneTransportFactory
    {
        /// <summary>
        /// Always throws: the fnecore-backed transport adapter is a
        /// later slice.
        /// </summary>
        /// <param name="system">The system configuration to connect to.</param>
        /// <returns>Never returns.</returns>
        /// <exception cref="PlatformNotSupportedException">Always.</exception>
        public IFneTransport Create(Codeplug.System system)
            => throw new PlatformNotSupportedException(
                "The fnecore-backed FNE transport adapter is a later slice; "
                + "no FNE transport can be created yet.");
    }
}
