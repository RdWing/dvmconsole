// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class FneSubscriberCommandIntegrationTests
{
    [Fact]
    public async Task SharedAdapterPublishesOnlyAcknowledgementsAddressedToThisConsole()
    {
        await using var master = new FneLoopbackMaster();
        await using var radio = new FneRadioSessionAdapter(master.CreateOptions() with { SourceId = 890 }, () => []);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        radio.ConnectionStateChanged += (_, _) =>
        {
            if (radio.IsConnected) connected.TrySetResult();
        };
        var acknowledgements = System.Threading.Channels.Channel.CreateUnbounded<ConsoleSubscriberAcknowledgement>();
        radio.SubscriberAcknowledged += (_, _) => throw new InvalidOperationException("Detached acknowledgement observer.");
        radio.SubscriberAcknowledged += (_, response) => acknowledgements.Writer.TryWrite(response);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await radio.StartAsync(timeout.Token);
        await connected.Task.WaitAsync(timeout.Token);
        foreach (var command in Enum.GetValues<P25SubscriberCommand>())
        {
            await master.SendSubscriberAcknowledgementAsync(command, 54321, 999, timeout.Token);
            uint target = command is P25SubscriberCommand.Inhibit or P25SubscriberCommand.Uninhibit
                ? fnecore.P25.P25Defines.WUID_FNE : 890;
            await master.SendSubscriberAcknowledgementAsync(command, 12345, target, timeout.Token);
            var response = await acknowledgements.Reader.ReadAsync(timeout.Token);
            Assert.Equal(new ConsoleSubscriberAcknowledgement(radio.SystemId,
                FneSubscriberCommandBindings.ToApplication(command), 12345), response);
        }
        Assert.False(acknowledgements.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisposingLoopbackCompletesPendingSubscriberRead()
    {
        await using var master = new FneLoopbackMaster();
        Task<byte[]> pending = master.ReadSubscriberCommandAsync().AsTask();
        await master.DisposeAsync();
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SharedCommandsReachLoopbackWithExpectedSubscriberPayloads()
    {
        await using var master = new FneLoopbackMaster();
        var options = master.CreateOptions();
        var configuration = new ConsoleConfiguration
        {
            Systems = [new() { Name = options.Name, Identity = options.Identity, Address = "127.0.0.1",
                Port = master.Port, PeerId = options.PeerId, Password = options.Password, Rid = "890" }],
            Zones = [new() { Name = "Fixture", Channels = [new() { Name = "Fixture", System = options.Name,
                Mode = "p25", Tgid = "100", RxOnly = true }] }]
        };
        await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (state, _) =>
        {
            var radios = FneConsoleRadioSessions.Prepare(state,
                configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                channel => new ChannelConfigurationAccess(channel.Runtime.Definition));
            var host = new ConsoleHostServices(radios, null!, null!, null!, null!, null!, null!,
                SystemClock.Instance, new BackgroundApplicationScheduler(_ => { }), SystemApplicationDelay.Instance, null!, []);
            return new(host, radios.Systems, FneReceiveFrameNormalization.Instance);
        });
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionStatesChanged += (_, _) => throw new InvalidOperationException("Detached connection observer.");
        session.ConnectionStatesChanged += (_, _) =>
        {
            if (session.SubscriberTargets.Single().IsConnected) connected.TrySetResult();
        };
        await session.ConnectAsync();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await FneSubscriberCommandDiagnostics.RunAsync(session, master, SystemId.FromName(options.Name));
        Assert.Equal(4, session.SubscriberCommandHistory.Count);
        Assert.All(session.SubscriberCommandHistory, result =>
        {
            Assert.True(result.Submitted);
            Assert.Equal(ConsoleSubscriberAcknowledgementState.Received, result.Acknowledgement);
        });
    }
}
