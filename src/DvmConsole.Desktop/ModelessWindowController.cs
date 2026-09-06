// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

/// <summary>
/// Owns modeless-window identity and close policy independently from the shell
/// event adapters. Session-bound windows close on replacement; application
/// windows remain until final shutdown.
/// </summary>
internal sealed class ModelessWindowController
{
    private sealed record Entry(object Window, bool SessionBound, Action Close);

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public TWindow? Get<TWindow>(string key)
        where TWindow : class
        => entries.TryGetValue(key, out Entry? entry) ? entry.Window as TWindow : null;

    public void Track<TWindow>(
        string key,
        TWindow window,
        bool sessionBound,
        Action<TWindow> close)
        where TWindow : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(close);
        if (entries.TryGetValue(key, out Entry? existing) && !ReferenceEquals(existing.Window, window))
            throw new InvalidOperationException($"A different modeless window is already tracked as '{key}'.");
        entries[key] = new Entry(window, sessionBound, () => close(window));
    }

    public void Forget<TWindow>(string key, TWindow? expected = null)
        where TWindow : class
    {
        if (!entries.TryGetValue(key, out Entry? entry))
            return;
        if (expected is not null && !ReferenceEquals(entry.Window, expected))
            return;
        entries.Remove(key);
    }

    public void CloseSessionBound()
        => CloseWhere(static entry => entry.SessionBound);

    public void CloseAll()
        => CloseWhere(static _ => true);

    private void CloseWhere(Func<Entry, bool> predicate)
    {
        KeyValuePair<string, Entry>[] closing = entries.Where(candidate => predicate(candidate.Value)).ToArray();
        foreach (KeyValuePair<string, Entry> entry in closing)
            entries.Remove(entry.Key);
        List<Exception>? failures = null;
        foreach (KeyValuePair<string, Entry> entry in closing)
        {
            try
            {
                entry.Value.Close();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is not null)
            throw new AggregateException("One or more modeless windows could not be closed.", failures);
    }
}
