// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Compiled-binding-friendly display option for a configured FNE system.
    /// The original codeplug object remains available to the command VM but is
    /// not used as an Avalonia DataTemplate type.
    /// </summary>
    public sealed class SubscriberCommandSystemOption
    {
        public SubscriberCommandSystemOption(Codeplug.System system)
        {
            System = system ?? throw new ArgumentNullException(nameof(system));
        }

        internal Codeplug.System System { get; }

        public string Name => System.Name;
    }
}
