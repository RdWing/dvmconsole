using DvmConsole.Audio;
using DvmConsole.Ptt;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PttSessionControllerTests
{
    [Fact]
    public async Task SerialEventsUseAppliedScopeInsteadOfUnappliedPresentationState()
    {
        var source = new TestPttSource([]);
        var settings = CreateSettings();
        PttTargetScope appliedScope = PttTargetScope.AllSelectedResources;
        var controller = new PttSessionController(
            settings,
            (_, _) => source,
            () => appliedScope);
        controller.CreateInitialSerialSource();
        var changes = new List<PttSourceStateChange>();
        controller.StateChanged += (_, change) => changes.Add(change);
        controller.AttachEvents();
        await controller.StartAsync();

        settings.SerialPttActiveSystemOnly = true;
        source.Raise(true);
        appliedScope = PttTargetScope.ActiveSystem;
        source.Raise(false);

        Assert.Equal(
            [
                new PttSourceStateChange(
                    true,
                    PttTargetScope.AllSelectedResources,
                    PttActivationSource.SerialHardware),
                new PttSourceStateChange(
                    false,
                    PttTargetScope.ActiveSystem,
                    PttActivationSource.SerialHardware)
            ],
            changes);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task SerialReplacementPreservesShutdownPersistConstructionOrder()
    {
        var operations = new List<string>();
        int sourceNumber = 0;
        var settings = CreateSettings();
        var controller = new PttSessionController(
            settings,
            (_, _) =>
            {
                sourceNumber++;
                operations.Add($"create-{sourceNumber}");
                return new TestPttSource(operations);
            },
            () => PttTargetScope.AllSelectedResources);
        controller.CreateInitialSerialSource();
        controller.AttachEvents();
        await controller.StartAsync();
        operations.Clear();

        await controller.ReplaceSerialSourceAsync(
            enabled: true,
            "replacement",
            19_200,
            () => operations.Add("persist"));

        Assert.Equal(
            ["stop", "dispose", "persist", "create-2", "start"],
            operations);
        await controller.DisposeAsync();
    }

    private static PttSettingsViewModel CreateSettings()
        => new(
            KeyboardPttKey.None,
            KeyboardPttKey.None,
            togglePttMode: false,
            serialPttEnabled: true,
            serialPttActiveSystemOnly: false,
            serialPttPortName: "initial",
            serialPttBaudRate: 9_600);

    private sealed class TestPttSource(List<string> operations) : IPttSource
    {
        public event EventHandler<bool>? StateChanged;

        public bool IsPressed { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("start");
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("stop");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            operations.Add("dispose");
            return ValueTask.CompletedTask;
        }

        public void Raise(bool pressed)
        {
            IsPressed = pressed;
            StateChanged?.Invoke(this, pressed);
        }
    }
}
