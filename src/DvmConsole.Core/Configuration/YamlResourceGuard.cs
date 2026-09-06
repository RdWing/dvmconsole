// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;
using DvmConsole.Core.IO;
using YamlDotNet.RepresentationModel;

namespace DvmConsole.Core.Configuration;

internal static class YamlResourceGuard
{
    private const int MaximumNodes = 100_000;
    private const int MaximumDepth = 64;

    public static void ValidateSource(string yaml, string documentName)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > ManagedResourceLimits.ConfigurationYamlBytes)
            throw new InvalidDataException($"The {documentName} exceeds the 8 MiB safety limit.");
    }

    public static void ValidateTree(YamlStream stream, string documentName)
    {
        var pending = new Stack<(YamlNode Node, int Depth)>();
        foreach (YamlDocument document in stream.Documents)
            pending.Push((document.RootNode, 1));

        int nodes = 0;
        while (pending.TryPop(out (YamlNode Node, int Depth) item))
        {
            if (item.Depth > MaximumDepth)
                throw new InvalidDataException($"The {documentName} exceeds the maximum YAML depth of {MaximumDepth}.");
            if (++nodes > MaximumNodes)
                throw new InvalidDataException($"The {documentName} exceeds the maximum YAML node count of {MaximumNodes:N0}.");

            if (item.Node is YamlMappingNode mapping)
            {
                foreach ((YamlNode key, YamlNode value) in mapping.Children)
                {
                    pending.Push((key, item.Depth + 1));
                    pending.Push((value, item.Depth + 1));
                }
            }
            else if (item.Node is YamlSequenceNode sequence)
            {
                foreach (YamlNode child in sequence.Children)
                    pending.Push((child, item.Depth + 1));
            }
        }
    }
}
