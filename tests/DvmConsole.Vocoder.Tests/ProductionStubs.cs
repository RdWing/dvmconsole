// SPDX-License-Identifier: AGPL-3.0-only
/**
* Test-only stand-ins for production types referenced by the linked
* dvmconsole/VocoderInterop.cs that live in other production files. These keep
* the interop slice compilable in isolation without pulling the whole WPF
* project (or the dead tone-code lookup table) into the test compile graph.
*
* VocoderToneLookupTable backs the unused MBEToneGenerator tone helpers, which
* are explicitly out of scope for this slice. It is intentionally empty.
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

namespace dvmconsole
{
    /// <summary>
    /// Minimal stand-in for dvmconsole/VocoderToneLookupTable.cs, referenced by
    /// MBEToneGenerator in the linked VocoderInterop.cs. Tone generation is out
    /// of scope for the vocoder interop slice.
    /// </summary>
    public static class VocoderToneLookupTable
    {
        public static Dictionary<ushort, byte[]> IMBEToneFrames { get; } =
            new Dictionary<ushort, byte[]>();
    }
}
