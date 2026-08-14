// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Core.Networking;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Presentation boundary for one P25 subscriber command dialog. It owns
    /// system/RID validation, duplicate-submit state, and result status; Core
    /// owns command policy and the injected executor owns protocol transport.
    /// </summary>
    public sealed class SubscriberCommandViewModel : INotifyPropertyChanged
    {
        private readonly Func<SubscriberCommandRequest, CancellationToken, Task<SubscriberCommandResult>> executeAsync;
        private readonly string ownerId;
        private SubscriberCommandSystemOption? selectedSystemOption;
        private string destinationId = string.Empty;
        private bool isSubmitting;
        private string statusMessage = string.Empty;

        public SubscriberCommandViewModel(
            IReadOnlyList<Codeplug.System?>? systems,
            SubscriberCommandKind commandKind,
            string ownerId,
            Func<SubscriberCommandRequest, CancellationToken, Task<SubscriberCommandResult>> executeAsync)
        {
            if (string.IsNullOrWhiteSpace(ownerId))
                throw new ArgumentException("An owner ID is required.", nameof(ownerId));

            this.ownerId = ownerId;
            this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            CommandKind = commandKind;

            var usableSystems = new List<SubscriberCommandSystemOption>();
            foreach (var system in systems ?? Array.Empty<Codeplug.System?>())
            {
                if (system is not null && !string.IsNullOrWhiteSpace(system.Name))
                    usableSystems.Add(new SubscriberCommandSystemOption(system));
            }

            Systems = usableSystems.AsReadOnly();
            selectedSystemOption = Systems.Count == 0 ? null : Systems[0];
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public IReadOnlyList<SubscriberCommandSystemOption> Systems { get; }

        public SubscriberCommandSystemOption? SelectedSystemOption
        {
            get => selectedSystemOption;
            set
            {
                if (ReferenceEquals(selectedSystemOption, value))
                    return;

                selectedSystemOption = value;
                OnPropertyChanged(nameof(SelectedSystemOption));
                OnPropertyChanged(nameof(SelectedSystem));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }

        public Codeplug.System? SelectedSystem => SelectedSystemOption?.System;

        public string DestinationId
        {
            get => destinationId;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(destinationId, normalized, StringComparison.Ordinal))
                    return;

                destinationId = normalized;
                OnPropertyChanged(nameof(DestinationId));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }

        public SubscriberCommandKind CommandKind { get; }

        public SubscriberCommandMode Mode => SubscriberCommandMode.P25;

        public bool IsSubmitting => isSubmitting;

        public bool CanSubmit
            => !IsSubmitting
                && IsPositiveId(SelectedSystem?.Rid)
                && IsPositiveId(DestinationId);

        public string StatusMessage => statusMessage;

        public async Task<SubscriberCommandResult> SubmitAsync(
            CancellationToken cancellationToken = default)
        {
            if (IsSubmitting)
            {
                var busy = new SubscriberCommandResult(
                    SubscriberCommandStatus.Busy,
                    "Another subscriber command is already active for this window.");
                SetStatus(busy.Message);
                return busy;
            }

            if (!CanSubmit)
            {
                var invalid = new SubscriberCommandResult(
                    SubscriberCommandStatus.InvalidRequest,
                    "Select a system and enter a valid destination ID.");
                SetStatus(invalid.Message);
                return invalid;
            }

            isSubmitting = true;
            OnPropertyChanged(nameof(IsSubmitting));
            OnPropertyChanged(nameof(CanSubmit));

            try
            {
                var request = new SubscriberCommandRequest(
                    ownerId,
                    SelectedSystem!.Name,
                    SelectedSystem.Rid,
                    DestinationId,
                    CommandKind,
                    Mode);

                SubscriberCommandResult result;
                try
                {
                    result = await executeAsync(request, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result = new SubscriberCommandResult(
                        cancellationToken.IsCancellationRequested
                            ? SubscriberCommandStatus.Cancelled
                            : SubscriberCommandStatus.TimedOut,
                        cancellationToken.IsCancellationRequested
                            ? "Subscriber command cancelled."
                            : "Subscriber command timed out.");
                }
                catch (Exception exception)
                {
                    result = new SubscriberCommandResult(
                        SubscriberCommandStatus.Failed,
                        $"Subscriber command failed: {exception.Message}");
                }

                SetStatus(result.Message);
                return result;
            }
            finally
            {
                isSubmitting = false;
                OnPropertyChanged(nameof(IsSubmitting));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }

        private void SetStatus(string message)
        {
            if (string.Equals(statusMessage, message, StringComparison.Ordinal))
                return;

            statusMessage = message;
            OnPropertyChanged(nameof(StatusMessage));
        }

        private static bool IsPositiveId(string? value)
            => uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
                && parsed != 0;

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
