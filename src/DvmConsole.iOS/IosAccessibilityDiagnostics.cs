// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Core.Configuration;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using DvmConsole.Storage;
using Foundation;
using ObjCRuntime;

namespace DvmConsole.iOS;

internal static class IosAccessibilityDiagnostics
{
    public static async Task<string> RunAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(RunOnUiAsync);
        return "PASS\nNative accessibility container: named controls, native activation selector, shared button/toggle/range actions, live value updates, disabled admission, retired-element rejection, focus-safe nested scrolling and production Studio review text/acceptance. Does not qualify VoiceOver speech, text editing or manual PTT.";
    }

    private static async Task RunOnUiAsync()
    {
        var app = (App)Avalonia.Application.Current!;
        var lifetime = (ISingleViewApplicationLifetime)app.ApplicationLifetime!;
        var shell = (Border)lifetime.MainView!;
        var previous = shell.Child;
        int invoked = 0;
        var button = new Button { Content = "Invoke check" };
        button.Click += (_, _) => invoked++;
        var choice = new ComboBox { ItemsSource = new[] { "First choice", "Second choice" }, SelectedIndex = 0 };
        AutomationProperties.SetName(choice, "Choice check");
        var toggle = new CheckBox { Content = "Toggle check" };
        var range = new Slider { Minimum = 0, Maximum = 10, Value = 5, SmallChange = 1 };
        AutomationProperties.SetName(range, "Range check");
        var editor = new TextBox { Text = "Editable value" };
        AutomationProperties.SetName(editor, "Edit check");
        var password = new TextBox { Text = "private diagnostic value", PasswordChar = '•' };
        AutomationProperties.SetName(password, "Secure edit check");
        bool togglePtt = false;
        int pttActivations = 0;
        var pttButton = new Button { Content = "PTT activation check" };
        using var pttBinding = new DvmConsole.Presentation.PttAccessibilityBinding(pttButton,
            () => togglePtt, () => pttActivations > 0,
            () => { pttActivations++; return ValueTask.CompletedTask; },
            () => { pttActivations--; return ValueTask.CompletedTask; }, exception => throw exception);
        int nestedInvoked = 0;
        var nestedButton = new Button { Content = "Nested row action" };
        nestedButton.Click += (_, _) => nestedInvoked++;
        var row = new ListBoxItem
        {
            Content = new StackPanel
            {
                Children = { new TextBlock { Text = "Selectable row" }, nestedButton }
            }
        };
        AutomationProperties.SetName(row, "Selectable row");
        var rows = new ListBox { ItemsSource = new[] { row }, Height = 80 };
        shell.Child = new StackPanel { Children = { button, toggle, range, choice, editor, password, pttButton, rows,
            new NativeControlHost { Width = 20, Height = 20 } } };
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var top = TopLevel.GetTopLevel(shell)!;
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            top.RequestAnimationFrame(_ => rendered.TrySetResult());
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var bridge = app.AccessibilityBridge ?? throw new InvalidOperationException("Native accessibility container was not attached.");
            using var native = bridge.ReadElements();
            var entries = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(native.Handle) ?? [])
                .OfType<IosAccessibilityBridge.PeerElement>().ToArray();
            var pttAction = entries.Single(element => element.AccessibilityLabel == "PTT activation check");
            if (pttAction.Activate() || pttActivations != 0)
                throw new InvalidOperationException("Accessible activation invented a held PTT press.");
            togglePtt = true;
            if (!pttAction.Activate() || pttActivations != 1)
                throw new InvalidOperationException("Accessible toggle PTT did not activate.");
            togglePtt = false;
            if (!pttAction.Activate() || pttActivations != 0)
                throw new InvalidOperationException("Accessible PTT release was blocked by the mode change.");
            var editAction = entries.Single(element => element.AccessibilityLabel == "Edit check");
            if (editAction.AccessibilityValue != editor.Text ||
                entries.Single(element => element.AccessibilityLabel == "Secure edit check").AccessibilityValue != "Secure text")
                throw new InvalidOperationException("Text value exposure did not honor secure input.");
            if (!editAction.Activate() || !editor.IsFocused)
                throw new InvalidOperationException("Editable text did not acquire input focus.");
            editor.IsReadOnly = true;
            if (editAction.Activate()) throw new InvalidOperationException("Read-only text admitted editing.");
            if (entries.Count(element => element.AccessibilityLabel == "Selectable row") != 1)
                throw new InvalidOperationException("Selectable row repeated its decorative label.");
            var nested = entries.Single(element => element.AccessibilityLabel == "Nested row action");
            if (!nested.Activate() || nestedInvoked != 1)
                throw new InvalidOperationException("Selectable row concealed its nested action.");
            editor.IsReadOnly = false;
            editor.Focus();
            bool editCommitted = false;
            bool invokedAfterCommit = false;
            editor.LostFocus += (_, _) => editCommitted = true;
            button.Click += (_, _) => invokedAfterCommit = editCommitted;
            var action = entries.Single(element => element.AccessibilityLabel == "Invoke check");
            if (!action.RespondsToSelector(new Selector("accessibilityActivate")) || !action.Activate() || invoked != 1)
                throw new InvalidOperationException("Native activation did not invoke the shared button.");
            if (!invokedAfterCommit || editor.IsFocused)
                throw new InvalidOperationException("Native button invocation bypassed the pending field commit.");
            button.IsEnabled = false;
            if (action.Activate() || invoked != 1) throw new InvalidOperationException("Disabled action was admitted.");
            var check = entries.Single(element => element.AccessibilityLabel == "Toggle check");
            if (!check.Activate() || toggle.IsChecked != true || check.AccessibilityValue != "On")
                throw new InvalidOperationException("Toggle action did not expose its new value immediately.");
            toggle.IsChecked = false;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (check.AccessibilityValue != "Off")
                throw new InvalidOperationException("Programmatic toggle state was stale without a tree read.");
            var slider = entries.Single(element => element.AccessibilityLabel == "Range check");
            slider.Increment();
            if (range.Value != 6) throw new InvalidOperationException("Range increment failed.");
            slider.Decrement();
            if (range.Value != 5) throw new InvalidOperationException("Range decrement failed.");
            var choiceAction = entries.Single(element => element.AccessibilityLabel == "Choice check");
            if (!choiceAction.Activate() || !choice.IsDropDownOpen)
                throw new InvalidOperationException("Native combo activation did not expand the choices.");
            await FrameAsync(shell);
            using (var expanded = bridge.ReadElements())
            {
                var options = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(expanded.Handle) ?? [])
                    .OfType<IosAccessibilityBridge.PeerElement>();
                var second = options.Single(element => element.AccessibilityLabel == "Second choice");
                if (!second.Activate() || choice.SelectedIndex != 1 || choice.IsDropDownOpen)
                    throw new InvalidOperationException("Native combo option did not select its value and close the picker.");
            }
            await FrameAsync(shell);
            if (!choiceAction.Activate()) throw new InvalidOperationException("Native combo did not reopen.");
            await FrameAsync(shell);
            using (var reopened = bridge.ReadElements())
            {
                var current = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(reopened.Handle) ?? [])
                    .OfType<IosAccessibilityBridge.PeerElement>()
                    .Single(element => element.AccessibilityLabel == "Second choice");
                if (!current.Activate() || choice.IsDropDownOpen || choice.SelectedIndex != 1)
                    throw new InvalidOperationException("Choosing the current value did not close the picker.");
            }
            await FrameAsync(shell);
            check.BecameFocused();
            shell.Child = new TextBlock { Text = "Replacement" };
            button.IsEnabled = true;
            if (action.Activate() || invoked != 1) throw new InvalidOperationException("Detached element was admitted before tree refresh.");
            using var replacement = bridge.ReadElements();
            if (action.Activate() || invoked != 1) throw new InvalidOperationException("Retired element was admitted.");
            await CheckScrollingAsync(shell, bridge);
            await CheckStudioKeyFocusAsync(shell, bridge);
            await CheckReviewContentAsync(shell, bridge);
        }
        finally { shell.Child = previous; }
    }
    private static async Task CheckReviewContentAsync(Border shell, IosAccessibilityBridge bridge)
    {
        const string message = "Review these configuration changes before saving.\n\n" +
            "The configuration, encryption keys, and aliases will be copied into app-owned storage. " +
            "The saved configuration is an immutable revision.\n\n" +
            "This diagnostic contains no private configuration or key material.";
        bool? accepted = null;
        var prompt = new MobileConfirmationPrompt("Review content check", message, "Save review check",
            result => accepted = result) { MaxHeight = 180 };
        var header = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(8, 0) };
        header.Children.Add(new TextBlock { Text = "Studio review diagnostic" });
        header.Children.Add(new ContentControl { Content = prompt });
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Width = 280 };
        layout.Children.Add(header);
        var editor = new Border { Height = 300, IsEnabled = false };
        Grid.SetRow(editor, 1);
        layout.Children.Add(editor);
        shell.Child = layout;
        await FrameAsync(shell);
        using var native = bridge.ReadElements();
        var entries = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(native.Handle) ?? [])
            .OfType<IosAccessibilityBridge.PeerElement>().ToArray();
        var content = entries.SingleOrDefault(element => element.AccessibilityLabel == message)
            ?? throw new InvalidOperationException("Native review content omitted its wrapped explanatory text.");
        if (content.AccessibilityFrameInContainerSpace.Height <= 0 ||
            (content.AccessibilityTraits & (ulong)UIKit.UIAccessibilityTrait.StaticText) == 0)
            throw new InvalidOperationException("Native review content has no readable text geometry or trait.");
        content.BecameFocused();
        if (!content.Scroll(UIKit.UIAccessibilityScrollDirection.Down))
            throw new InvalidOperationException("Long review content could not scroll its containing prompt.");
        await FrameAsync(shell);
        using var scrolled = bridge.ReadElements();
        var save = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(scrolled.Handle) ?? [])
            .OfType<IosAccessibilityBridge.PeerElement>()
            .Single(element => element.AccessibilityLabel == "Save review check");
        if (!save.Activate() || accepted != true)
            throw new InvalidOperationException("Native Studio review action did not complete the production prompt.");
    }

    private static async Task CheckScrollingAsync(Border shell, IosAccessibilityBridge bridge)
    {
        var rows = new StackPanel();
        for (int index = 0; index < 20; index++)
            rows.Children.Add(new Button { Content = $"Scroll row {index}", Height = 60 });
        var inner = new ScrollViewer { Content = rows, Height = 120 };
        var outer = new ScrollViewer
        {
            Height = 240,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Content = new StackPanel { Children = { inner, new Border { Height = 1000 } } }
        };
        shell.Child = outer;
        await FrameAsync(shell);
        using var initial = bridge.ReadElements();
        var entries = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(initial.Handle) ?? [])
            .OfType<IosAccessibilityBridge.PeerElement>().ToArray();
        // The focused toggle was retired when the previous page was replaced.
        // Scrolling must fall back to this page's containers before a new item is focused.
        if (!bridge.Scroll(UIKit.UIAccessibilityScrollDirection.Down) || outer.Offset.Y <= 0)
            throw new InvalidOperationException("Retired focus prevented scrolling the replacement page.");
        outer.Offset = default;
        await FrameAsync(shell);
        var first = entries.First(element => element.AccessibilityLabel == "Scroll row 0");
        first.BecameFocused();
        if (!bridge.Scroll(UIKit.UIAccessibilityScrollDirection.Down))
            throw new InvalidOperationException("Nested scroll action was rejected.");
        await FrameAsync(shell);
        if (inner.Offset.Y <= 0 || outer.Offset.Y != 0)
            throw new InvalidOperationException("Scroll did not choose the nearest container.");
        inner.Offset = new Avalonia.Vector(0, inner.Extent.Height);
        await FrameAsync(shell);
        using var atEnd = bridge.ReadElements();
        var last = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(atEnd.Handle) ?? [])
            .OfType<IosAccessibilityBridge.PeerElement>()
            .Last(element => element.AccessibilityLabel?.StartsWith("Scroll row ", StringComparison.Ordinal) == true);
        last.BecameFocused();
        if (!bridge.Scroll(UIKit.UIAccessibilityScrollDirection.Down))
            throw new InvalidOperationException("Nested boundary did not hand scrolling to its parent.");
        await FrameAsync(shell);
        if (outer.Offset.Y <= 0) throw new InvalidOperationException("Parent scroll offset did not advance.");
    }

    private static async Task CheckStudioKeyFocusAsync(Border shell, IosAccessibilityBridge bridge)
    {
        var model = new ConfigurationStudioViewModel(ConfigurationDocument.CreateNew(), null,
            "accessibility-draft.yml", new MobileStudioRuntimeContext(),
            new MaterializedConfigurationStudioCompanionSource(), new ConfigurationSnapshotPreviewFactory(),
            new(new Dictionary<string, ConfigurationStudioPosition>(), new Dictionary<string, string>(), []),
            ConfigurationStudioSection.EncryptionKeys);
        model.AddSystem();
        model.AddKey();
        model.SelectSection(ConfigurationStudioSection.EncryptionKeys);
        var studio = new ConfigurationStudioView { UseTouchLayout = true, DataContext = model };
        shell.Child = studio;
        await FrameAsync(shell);
        var keys = studio.FindControl<ConfigurationStudioKeysView>("KeysPage")!;
        var material = keys.GetVisualDescendants().OfType<TextBox>().Single(control =>
            AutomationProperties.GetName(control) == "Encryption key material");
        var algorithm = keys.GetVisualDescendants().OfType<ComboBox>().Single(control =>
            AutomationProperties.GetName(control) == "Encryption key algorithm");
        using (var initial = bridge.ReadElements())
        {
            var key = model.KeyEntries.Single();
            if (!(NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(initial.Handle) ?? [])
                .OfType<IosAccessibilityBridge.PeerElement>().Any(element =>
                    element.AccessibilityLabel == $"{key.Name}, {key.SystemDisplayName}, key {key.KeyIdText}"))
                throw new InvalidOperationException("Studio key row did not expose its name, system and key ID.");
        }
        material.BringIntoView();
        await FrameAsync(shell);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            material.Focus();
            material.Text = new string(cycle == 0 ? '0' : '1', 64);
            using var native = bridge.ReadElements();
            var elements = (NSArray.ArrayFromHandle<IosAccessibilityBridge.PeerElement>(native.Handle) ?? [])
                .OfType<IosAccessibilityBridge.PeerElement>().ToArray();
            var algorithmAction = elements.Single(element => element.AccessibilityLabel == "Encryption key algorithm");
            if (!algorithmAction.Activate())
                throw new InvalidOperationException("Studio algorithm picker did not activate after editing key material.");
            await FrameAsync(shell);
            if (!algorithm.IsDropDownOpen || model.KeyEntries.Single().Key != material.Text)
                throw new InvalidOperationException("Studio focus transition lost its key edit or closed its picker.");
            algorithm.IsDropDownOpen = false;
            await FrameAsync(shell);
        }
    }

    private static async Task FrameAsync(Control control)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TopLevel.GetTopLevel(control)!.RequestAnimationFrame(_ => rendered.TrySetResult());
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

}
