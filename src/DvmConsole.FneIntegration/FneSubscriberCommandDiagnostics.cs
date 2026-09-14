// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;
using fnecore.EDAC;
using fnecore.P25;

namespace DvmConsole.FneIntegration;

/// <summary>Explicit loopback qualification against the independent subscriber payload fixtures.</summary>
public static class FneSubscriberCommandDiagnostics
{
    public static async Task RunAsync(ConsoleReceiveSession session, FneLoopbackMaster master, SystemId system)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var command in Enum.GetValues<ConsoleSubscriberCommand>())
        {
            var result = await session.SendSubscriberCommandAsync(system, command, 12345, timeout.Token);
            if (!result.Submitted) throw new InvalidOperationException(result.Detail);
            byte[] frame = await master.ReadSubscriberCommandAsync(timeout.Token);
            if (frame[5] != 0 || frame[6] != 3 || frame[7] != 0x7A ||
                frame[8] != 0 || frame[9] != 0x30 || frame[10] != 0x39)
                throw new InvalidOperationException("Subscriber command source or target changed on the wire.");
            byte[] encoded = new byte[P25Defines.P25_TSBK_FEC_LENGTH_BYTES];
            P25Interleaver.Decode(frame[24..], ref encoded, 114, 318);
            byte[] tsbk = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];
            if (!new Trellis().Decode12(encoded, ref tsbk))
                throw new InvalidOperationException("Subscriber command FEC did not decode.");
            string expected = command switch
            {
                ConsoleSubscriberCommand.Page => "000000303900037A",
                ConsoleSubscriberCommand.RadioCheck => "000000037A003039",
                ConsoleSubscriberCommand.Inhibit => "007FFFFFFC003039",
                ConsoleSubscriberCommand.Uninhibit => "007EFFFFFC003039",
                _ => throw new InvalidOperationException("Unknown subscriber qualification command.")
            };
            if (!tsbk.AsSpan(2, 8).SequenceEqual(Convert.FromHexString(expected)))
                throw new InvalidOperationException($"{command} did not match the subscriber command fixture.");
            uint target = command is ConsoleSubscriberCommand.Inhibit or ConsoleSubscriberCommand.Uninhibit
                ? P25Defines.WUID_FNE : 890;
            await master.SendSubscriberAcknowledgementAsync(FneSubscriberCommandBindings.ToFne(command), 12345, target, timeout.Token);
            while (session.SubscriberCommandHistory.Single(entry => entry.Id == result.Id).Acknowledgement !=
                   ConsoleSubscriberAcknowledgementState.Received)
                await Task.Delay(10, timeout.Token);
        }
    }
}
