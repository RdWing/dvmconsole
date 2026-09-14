// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Media;

namespace DvmConsole.Storage;

/// <summary>Loads managed configuration key companions before radio construction.</summary>
public static class ConfigurationKeyRingLoader
{
    public static (P25KeyRing P25, DmrKeyRing Dmr, NxdnKeyRing Nxdn) Load(
        ConsoleConfiguration configuration,
        out string? warning)
    {
        var p25Ring = new P25KeyRing();
        var dmrRing = new DmrKeyRing();
        var nxdnRing = new NxdnKeyRing();
        warning = null;
        if (string.IsNullOrWhiteSpace(configuration.KeyFile))
            return (p25Ring, dmrRing, nxdnRing);

        try
        {
            KeyContainer localKeys = KeyFileLoader.Load(
                ConfigurationLoader.ResolvePath(configuration, configuration.KeyFile));
            foreach (SystemConfiguration system in configuration.Systems)
            {
                p25Ring.AddLocalKeys(system.Name, localKeys);
                dmrRing.AddLocalKeys(system.Name, localKeys);
                nxdnRing.AddLocalKeys(system.Name, localKeys);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            warning = $"Encryption keys unavailable: {exception.Message} Clear receive remains available. Secure transmit and encrypted receive require the applicable key; P25 keys may arrive from FNE/KMM, while DMR and NXDN use local keys.";
            p25Ring.Dispose();
            dmrRing.Dispose();
            nxdnRing.Dispose();
            return (new P25KeyRing(), new DmrKeyRing(), new NxdnKeyRing());
        }
        catch
        {
            p25Ring.Dispose();
            dmrRing.Dispose();
            nxdnRing.Dispose();
            throw;
        }
        return (p25Ring, dmrRing, nxdnRing);
    }

}
