// SPDX-License-Identifier: AGPL-3.0-only
/**
* Test-only stand-ins for production types referenced by the linked
* dvmconsole/Codeplug.cs that live in other production files. These keep the
* codeplug contract slice compilable in isolation without pulling the whole
* WPF project into the test compile graph.
*
* Why this is a stub instead of a linked production file: Codeplug.cs uses
* dvmconsole.RadioAlias, defined in dvmconsole/AliasTools.cs together with
* AliasTools.LoadAliases (which depends on System.IO file access). Linking
* AliasTools.cs wholesale would drag file-I/O surface into the compile slice
* for no contract value; the shape mirrored here is exactly the production
* declaration at AliasTools.cs lines 24-34. If the production shape changes,
* this stub must be updated to match or the compile will drift silently.
*/
namespace dvmconsole
{
    /// <summary>
    /// Minimal stand-in for dvmconsole/AliasTools.cs RadioAlias (lines 24-34).
    /// Must mirror the production shape exactly: a YAML-deserializable alias
    /// record with a display name and a radio ID.
    /// </summary>
    public class RadioAlias
    {
        /// <summary>
        /// Display alias text for the radio ID.
        /// </summary>
        public string Alias { get; set; }
        /// <summary>
        /// Numeric radio ID.
        /// </summary>
        public int Rid { get; set; }
    } // public class RadioAlias
}
