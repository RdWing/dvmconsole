// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

internal interface IConfigurationStudioCompanionCommandSession
{
    ConsoleConfiguration Configuration { get; }
    bool CanEditCompanions { get; }
    SystemConfiguration? SelectedSystemForCompanion { get; }
    SystemConfiguration? SelectedAliasSystemForCompanion { get; }
    KeyEntry? SelectedKeyForCompanion { get; }
    ConfigurationAliasRow? SelectedAliasForCompanion { get; }
    int KeyCount { get; }
    ConfigurationStudioDraftSnapshot CurrentDraft { get; }
    void EnsureKeyFile();
    void AddKey(KeyEntry key);
    void RemoveKey(KeyEntry key);
    (string Identifier, RadioAlias Alias) AddAlias(SystemConfiguration system);
    bool RemoveAlias(ConfigurationAliasRow row);
    void CompleteKeyAddition(ConfigurationStudioDraftSnapshot before, KeyEntry key);
    void CompleteKeyRemoval();
    void CompleteAliasAddition(
        ConfigurationStudioDraftSnapshot before,
        string identifier,
        RadioAlias alias);
    void CompleteAliasRemoval(ConfigurationAliasRow row);
}

/// <summary>
/// Owns key and alias companion commands while the Studio facade retains only
/// binding projection and transaction mechanics.
/// </summary>
internal sealed class ConfigurationStudioCompanionCommandController
{
    private readonly IConfigurationStudioCompanionCommandSession session;

    public ConfigurationStudioCompanionCommandController(
        IConfigurationStudioCompanionCommandSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void AddKey()
    {
        if (!session.CanEditCompanions)
            return;

        ConfigurationStudioDraftSnapshot before = session.CurrentDraft;
        session.EnsureKeyFile();
        EncryptionAlgorithmOption algorithm = EncryptionAlgorithmCatalog.ForKeyProtocol("p25")[0];
        var key = new KeyEntry
        {
            Name = $"Key {session.KeyCount + 1}",
            System = session.SelectedSystemForCompanion?.Name ??
                session.Configuration.Systems.FirstOrDefault()?.Name ??
                string.Empty,
            Protocol = algorithm.Protocol,
            KeyId = 1,
            AlgId = algorithm.AlgorithmId ?? 0,
            Key = string.Empty
        };
        session.AddKey(key);
        session.CompleteKeyAddition(before, key);
    }

    public void DeleteKey()
    {
        if (!session.CanEditCompanions || session.SelectedKeyForCompanion is not { } key)
            return;

        session.RemoveKey(key);
        session.CompleteKeyRemoval();
    }

    public void AddAlias()
    {
        if (!session.CanEditCompanions)
            return;
        SystemConfiguration? system = session.SelectedAliasSystemForCompanion ??
            session.SelectedSystemForCompanion ??
            session.Configuration.Systems.FirstOrDefault();
        if (system is null)
            return;

        ConfigurationStudioDraftSnapshot before = session.CurrentDraft;
        (string identifier, RadioAlias alias) = session.AddAlias(system);
        session.CompleteAliasAddition(before, identifier, alias);
    }

    public void DeleteAlias()
    {
        if (!session.CanEditCompanions ||
            session.SelectedAliasForCompanion is not { } row ||
            !session.RemoveAlias(row))
        {
            return;
        }

        session.CompleteAliasRemoval(row);
    }
}
