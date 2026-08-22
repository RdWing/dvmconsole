#nullable enable
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;

namespace fnecore;

internal sealed class InboundReplayWindow
{
    internal const int MaximumEntries = 4_096;

    private readonly object sync = new();
    private readonly HashSet<string> fingerprints = new(StringComparer.Ordinal);
    private readonly Queue<string> insertionOrder = new();

    public bool TryRemember(byte[] wire)
    {
        string fingerprint = Convert.ToBase64String(SHA256.HashData(wire));
        lock (sync)
        {
            if (!fingerprints.Add(fingerprint))
                return false;

            insertionOrder.Enqueue(fingerprint);
            while (insertionOrder.Count > MaximumEntries)
                fingerprints.Remove(insertionOrder.Dequeue());
            return true;
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            fingerprints.Clear();
            insertionOrder.Clear();
        }
    }
}
