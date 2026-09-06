// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class FneRadioSessionFactoryTests
{
    [Fact]
    public async Task DescriptorExcludesConnectionSecretsAndCreatesBoundSession()
    {
        var options = new FneConnectionOptions(
            "Primary",
            "console",
            "127.0.0.1",
            62031,
            1001,
            "password-secret",
            true,
            "transport-secret");
        var factory = new FneRadioSessionFactory(options, () => []);

        Assert.DoesNotContain(
            factory.Descriptor.ConnectionParameters.Values,
            value => value.Contains("secret", StringComparison.Ordinal));

        await using IFneRadioSession desktopSession = factory.Create();
        Assert.Equal(factory.Descriptor.Id, desktopSession.SystemId);

        await using IRadioSession session = await factory.CreateAsync(factory.Descriptor);
        Assert.Equal(factory.Descriptor.Id, session.SystemId);
    }

    [Fact]
    public async Task FactoryRejectsDescriptorForAnotherSystem()
    {
        var options = new FneConnectionOptions(
            "Primary",
            "console",
            "127.0.0.1",
            62031,
            1001,
            null,
            false,
            null);
        var factory = new FneRadioSessionFactory(options, () => []);
        RadioSystemDescriptor other = factory.Descriptor with
        {
            Id = SystemId.FromName("Other"),
            Name = "Other"
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await factory.CreateAsync(other));
    }
}
