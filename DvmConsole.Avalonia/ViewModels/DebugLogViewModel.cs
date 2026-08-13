// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Headless bounded presentation model over a shared
    /// <see cref="LogBuffer"/>. The shell owns subscription and dispatcher
    /// marshaling; this model only manages its visible snapshot.
    /// </summary>
    public sealed class DebugLogViewModel
    {
        private const int MaxVisibleLines = 500;

        /// <summary>
        /// Creates a viewer seeded from the buffer's current snapshot.
        /// </summary>
        public DebugLogViewModel(LogBuffer buffer)
        {
            Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

            foreach (string line in buffer.GetRecentLines())
                AppendLine(line);
        }

        /// <summary>The shared source buffer; never cleared by this model.</summary>
        public LogBuffer Buffer { get; }

        /// <summary>Oldest-first visible lines, bounded to 500 entries.</summary>
        public ObservableCollection<string> Lines { get; } = new();

        /// <summary>
        /// Appends one visible line and evicts the oldest line at the bound.
        /// The caller is responsible for UI-thread affinity.
        /// </summary>
        public void AppendLine(string line)
        {
            if (line is null)
                throw new ArgumentNullException(nameof(line));

            while (Lines.Count >= MaxVisibleLines)
                Lines.RemoveAt(0);

            Lines.Add(line);
        }

        /// <summary>Clears only this viewer's visible snapshot.</summary>
        public void Clear() => Lines.Clear();

        /// <summary>
        /// Returns a detached newline-joined snapshot suitable for clipboard
        /// or file export by the owning window.
        /// </summary>
        public string GetTextSnapshot() => string.Join(Environment.NewLine, Lines.ToArray());
    }
}
