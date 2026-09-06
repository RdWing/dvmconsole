// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Runtime.InteropServices;

namespace DvmConsole.Audio;

internal static class PipeWireNativeContract
{
    private const string ResourceName = "DvmConsole.Audio.Linux.ManagedExports.txt";
    private static readonly string[] requiredExports = LoadRequiredExports();

    public static IReadOnlyList<string> RequiredExports => requiredExports;

    public static void ValidateExports(IntPtr library)
    {
        if (library == IntPtr.Zero)
            throw new ArgumentException("A loaded PipeWire library handle is required.", nameof(library));

        string[] missing = requiredExports
            .Where(symbol => !NativeLibrary.TryGetExport(library, symbol, out _))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new EntryPointNotFoundException(
                $"The PipeWire audio shim is missing required exports: {string.Join(", ", missing)}.");
        }
    }

    private static string[] LoadRequiredExports()
    {
        using Stream stream = typeof(PipeWireNativeContract).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded PipeWire ABI manifest '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var exports = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            string symbol = line.Trim();
            if (symbol.Length > 0 && !symbol.StartsWith('#'))
                exports.Add(symbol);
        }
        if (exports.Count == 0 || exports.Count != exports.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("The PipeWire ABI manifest is empty or contains duplicates.");
        return exports.ToArray();
    }
}
