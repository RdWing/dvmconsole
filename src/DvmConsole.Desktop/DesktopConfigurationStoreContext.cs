// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Configuration.Yaml;

namespace DvmConsole.Desktop;

/// <summary>
/// Prepares configuration-library and materialization storage away from the UI
/// thread. Their compatibility constructors retain bounded synchronous locks,
/// while production startup awaits this context before constructing a window.
/// </summary>
internal sealed record DesktopConfigurationStoreContext(
    ManagedConfigurationLibrary Library,
    ManagedConfigurationCommandController Commands,
    DesktopConfigurationMaterializer Materializer)
{
    public static DesktopConfigurationStoreContext Create(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        string root = Path.GetFullPath(appDataRoot);
        var library = new ManagedConfigurationLibrary(Path.Combine(root, "ConfigurationLibrary"));
        return new DesktopConfigurationStoreContext(
            library,
            new ManagedConfigurationCommandController(library),
            new DesktopConfigurationMaterializer(
                library,
                Path.Combine(root, "ConfigurationRuntime")));
    }

    public static Task<DesktopConfigurationStoreContext> CreateAsync(
        string appDataRoot,
        Func<string, DesktopConfigurationStoreContext>? factory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        string root = Path.GetFullPath(appDataRoot);
        Func<string, DesktopConfigurationStoreContext> create = factory ?? Create;
        return Task.Run(() => create(root), cancellationToken);
    }
}
