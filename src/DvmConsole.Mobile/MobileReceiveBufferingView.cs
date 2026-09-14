// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Mobile;

internal sealed class MobileReceiveBufferingView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly Func<MobileSession> session;
    private readonly IConsoleApplicationSession owner;
    private readonly IConsoleReceiveBufferingSettings? settings;
    private readonly ComboBox systems = new() { MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<SystemId, SystemEditor> editors = [];
    private readonly ContentControl editor = new();
    private readonly StackPanel fields = new() { Spacing = 12 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private bool saving;

    public MobileReceiveBufferingView(Func<MobileSession> session)
    {
        this.session = session;
        owner = session().Application;
        settings = owner.Commands as IConsoleReceiveBufferingSettings;
        var back = new Button { Content = "‹ Connections", MinHeight = 48 };
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var connections = owner.Topology.Systems.Select(system => new Connection(system.Id, system.Name)).ToArray();
        systems.ItemsSource = connections;
        systems.ItemTemplate = new FuncDataTemplate<Connection>((item, _) => new TextBlock { Text = item?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        AutomationProperties.SetName(systems, "Receive Buffering connection");
        foreach (var connection in connections)
            editors[connection.Id] = new(settings?.ReceiveBuffering.GetValueOrDefault(connection.Id) ?? ConsoleReceiveBufferingOptions.Default);
        systems.SelectionChanged += (_, _) =>
        {
            if (systems.SelectedItem is Connection connection) editor.Content = editors[connection.Id];
        };
        fields.Children.Add(new TextBlock { Text = "Adaptive buffering learns packet delay variation for each FNE and protocol. Turn it off to choose a fixed delay; 0 ms disables buffering. Larger delays can smooth uneven traffic but add listening and patch latency. Changes apply to new receive streams.", TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(systems);
        fields.Children.Add(editor);
        var apply = new Button { Content = "Apply receive buffering", MinHeight = 48 };
        apply.Click += async (_, _) => await SaveAsync();
        fields.Children.Add(apply);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(fields);
        body.Children.Add(status);
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Receive Buffering", back), body);
        fields.IsEnabled = settings?.CanSaveReceiveBuffering == true && connections.Length > 0;
        if (!fields.IsEnabled) status.Text = "Open a configuration with an FNE connection first.";
        systems.SelectedIndex = connections.Length > 0 ? 0 : -1;
    }

    private async Task SaveAsync()
    {
        if (saving || settings?.CanSaveReceiveBuffering != true || systems.SelectedItem is not Connection connection) return;
        if (!ReferenceEquals(owner, session().Application))
        { status.Text = "The active configuration changed. Reopen receive buffering from Settings."; return; }
        saving = true;
        fields.IsEnabled = false;
        try
        {
            await settings.SetReceiveBufferingAsync(connection.Id, editors[connection.Id].Capture());
            status.Text = ReferenceEquals(owner, session().Application)
                ? "Saved for new receive streams." : "Saved for the previous configuration.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { saving = false; fields.IsEnabled = true; }
    }

    private sealed record Connection(SystemId Id, string Name);

    private sealed class SystemEditor : StackPanel
    {
        private readonly ProtocolEditor p25;
        private readonly ProtocolEditor dmr;
        private readonly ProtocolEditor nxdn;
        public SystemEditor(ConsoleReceiveBufferingOptions options)
        {
            Spacing = 12;
            p25 = new("P25", RxJitterBufferSetting.P25OptionsMilliseconds, options.P25Milliseconds, options.P25Adaptive);
            dmr = new("DMR", RxJitterBufferSetting.DmrOptionsMilliseconds, options.DmrMilliseconds, options.DmrAdaptive);
            nxdn = new("NXDN", RxJitterBufferSetting.NxdnOptionsMilliseconds, options.NxdnMilliseconds, options.NxdnAdaptive);
            Children.Add(p25);
            Children.Add(dmr);
            Children.Add(nxdn);
        }
        public ConsoleReceiveBufferingOptions Capture() => new(p25.Delay, p25.Adaptive,
            dmr.Delay, dmr.Adaptive, nxdn.Delay, nxdn.Adaptive);
    }

    private sealed class ProtocolEditor : StackPanel
    {
        private readonly CheckBox adaptive = new() { MinHeight = 48 };
        private readonly ComboBox delay = new() { MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch };
        public ProtocolEditor(string protocol, IReadOnlyList<int> choices, int milliseconds, bool isAdaptive)
        {
            Spacing = 4;
            adaptive.Content = new TextBlock { Text = protocol + " adaptive buffering", TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(adaptive, protocol + " adaptive buffering");
            AutomationProperties.SetName(delay, protocol + " fixed delay");
            delay.ItemsSource = choices;
            delay.ItemTemplate = new FuncDataTemplate<int>((value, _) => new TextBlock { Text = value + " ms" });
            delay.SelectedItem = milliseconds;
            adaptive.IsChecked = isAdaptive;
            delay.IsEnabled = !isAdaptive;
            adaptive.IsCheckedChanged += (_, _) => delay.IsEnabled = !Adaptive;
            Children.Add(adaptive);
            Children.Add(delay);
        }
        public bool Adaptive => adaptive.IsChecked == true;
        public int Delay => delay.SelectedItem is int value ? value : throw new InvalidOperationException("Choose a delay for every protocol.");
    }
}
