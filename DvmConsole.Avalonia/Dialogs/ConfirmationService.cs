// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;


namespace DvmConsole.Avalonia.Dialogs
{
    public sealed record ConfirmationRequest(string Title, string Message);

    /// <summary>Host-owned confirmation prompt used by destructive shell actions.</summary>
    public interface IConfirmationService
    {
        Task<bool> ConfirmAsync(
            Window owner,
            ConfirmationRequest request,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Small Fluent-compatible modal prompt kept in the Avalonia shell so the
    /// platform/core layers remain UI-free.
    /// </summary>
    public sealed class AvaloniaConfirmationService : IConfirmationService
    {
        public async Task<bool> ConfirmAsync(
            Window owner,
            ConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            if (owner is null)
                throw new ArgumentNullException(nameof(owner));
            if (request is null)
                throw new ArgumentNullException(nameof(request));

            var dialog = new Window
            {
                Title = request.Title,
                Width = 430,
                MinWidth = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = Brushes.White,
            };
            var result = false;
            var message = new TextBlock
            {
                Text = request.Message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new global::Avalonia.Thickness(0, 0, 0, 18),
            };
            var yes = new Button { Content = "Delete", MinWidth = 88 };
            var no = new Button { Content = "Cancel", MinWidth = 88 };
            yes.Click += (_, _) =>
            {
                result = true;
                dialog.Close();
            };
            no.Click += (_, _) => dialog.Close();
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children = { no, yes },
            };
            dialog.Content = new StackPanel
            {
                Margin = new global::Avalonia.Thickness(22),
                Children = { message, buttons },
            };

            using CancellationTokenRegistration registration = cancellationToken.Register(dialog.Close);
            await dialog.ShowDialog(owner);
            return result && !cancellationToken.IsCancellationRequested;
        }
    }

    /// <summary>Headless fallback that never confirms destructive actions.</summary>
    public sealed class NoopConfirmationService : IConfirmationService
    {
        public static NoopConfirmationService Instance { get; } = new NoopConfirmationService();

        private NoopConfirmationService()
        {
        }

        public Task<bool> ConfirmAsync(
            Window owner,
            ConfirmationRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
