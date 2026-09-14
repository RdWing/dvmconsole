// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>The native host retains app-owned document leases; navigation owns the editor lifetime.</summary>
public sealed class MobileStudioSession(
    ConfigurationStudioViewModel viewModel,
    ConfigurationStudioSaveServices saveServices,
    Func<IExportDocumentSet, bool, Task<IReadOnlyList<string>>> exportAsync,
    Func<ValueTask> dispose, string initialStatus = "",
    Func<CancellationToken, Task>? checkpoint = null) : IAsyncDisposable
{
    private int disposed;
    private readonly Lazy<Task> disposal = new(async () => await dispose().ConfigureAwait(false));
    public ConfigurationStudioViewModel ViewModel { get; } = viewModel;
    public string InitialStatus { get; } = initialStatus;
    public ConfigurationStudioSaveServices SaveServices { get; } = saveServices;
    public Func<IExportDocumentSet, bool, Task<IReadOnlyList<string>>> ExportAsync { get; } = exportAsync;
    public event Action<string>? CheckpointFailed;
    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0 || checkpoint is null) return;
        try { await checkpoint(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            CheckpointFailed?.Invoke($"Draft recovery checkpoint failed: {exception.Message}");
            throw;
        }
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return new ValueTask(disposal.Value);
    }
}

/// <summary>Draft geometry context. The mobile host supplies its live operator editor separately.</summary>
public sealed class MobileStudioRuntimeContext : IConfigurationStudioRuntimeContext
{
    public IReadOnlyList<PatchGroupEditorViewModel> OperationalGroups => [];
    public double DefaultCanvasWidth => 1000;
    public double CardSpacing => 8;
    public double CardHeight => 124;
    public double UiFontSize => 14;
    public double UiSmallFontSize => 12;
    public double UiCompactFontSize => 11;
    public bool DarkMode => true;
    public bool IsActiveConfiguration(DvmConsole.Application.ConfigurationId? id, string identity) => false;
    public double ResolveCardWidth(string? size) => ConsoleCardGeometry.ResolveWidth(size);
    public string? ApplyOperationalGroups(IEnumerable<PatchGroupEditorViewModel> groups)
        => "Use the active console group editor to apply operational changes.";
    public void SetOperationalGroupEnabled(PatchGroupEditorViewModel group)
        => throw new InvalidOperationException("Use the active console group editor to apply operational changes.");
}
