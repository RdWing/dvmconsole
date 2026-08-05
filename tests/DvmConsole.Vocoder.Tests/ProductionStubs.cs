// SPDX-License-Identifier: AGPL-3.0-only
/**
* Test-only stand-ins for production types referenced by the linked
* dvmconsole/VocoderInterop.cs that live in other production files. These keep
* the interop slice compilable in isolation without pulling the whole WPF
* project into the test compile graph.
*
* VocoderToneLookupTable is no longer stubbed here: it ships in the portable
* DvmConsole.Core assembly and is referenced via ProjectReference (see
* DvmConsole.Vocoder.Tests.csproj).
*/

namespace fnecore
{
    /// <summary>
    /// Minimal empty stand-in for the fnecore namespace. The linked
    /// VocoderInterop.cs carries `using fnecore;` but never references an
    /// fnecore type, so an empty namespace satisfies the name resolution
    /// without dragging the real fnecore project into the test compile graph.
    /// </summary>
}
