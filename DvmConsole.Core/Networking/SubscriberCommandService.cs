// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Core.Networking
{
    public enum SubscriberCommandKind
    {
        Page,
        RadioCheck,
        Inhibit,
        Uninhibit,
    }

    public enum SubscriberCommandMode
    {
        Dmr,
        P25,
    }

    public enum SubscriberCommandStatus
    {
        Succeeded,
        InvalidRequest,
        UnsupportedMode,
        Disconnected,
        Busy,
        Cancelled,
        TimedOut,
        Failed,
    }

    public sealed record SubscriberCommandRequest(
        string OwnerId,
        string SystemName,
        string SourceId,
        string DestinationId,
        SubscriberCommandKind Kind,
        SubscriberCommandMode Mode);

    public readonly record struct SubscriberCommandResult(
        SubscriberCommandStatus Status,
        string Message)
    {
        public bool Succeeded => Status == SubscriberCommandStatus.Succeeded;
    }

    public interface ISubscriberCommandTransport
    {
        Task SendAsync(
            SubscriberCommandRequest request,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Owns validation and lifecycle policy for one-shot subscriber commands.
    /// Protocol-specific packet construction stays behind ISubscriberCommandTransport.
    /// </summary>
    public sealed class SubscriberCommandService
    {
        private readonly Func<string, bool> isConnected;
        private readonly Func<string, ISubscriberCommandTransport?> resolveTransport;
        private readonly TimeSpan timeout;
        private readonly object ownerGate = new object();
        private readonly HashSet<string> activeOwners = new HashSet<string>(StringComparer.Ordinal);

        public SubscriberCommandService(
            Func<string, bool> isConnected,
            Func<string, ISubscriberCommandTransport?> resolveTransport,
            TimeSpan timeout)
        {
            this.isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
            this.resolveTransport = resolveTransport ?? throw new ArgumentNullException(nameof(resolveTransport));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            this.timeout = timeout;
        }

        public async Task<SubscriberCommandResult> ExecuteAsync(
            SubscriberCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            var validation = Validate(request);
            if (validation is not null)
                return validation.Value;

            if (request.Mode != SubscriberCommandMode.P25)
            {
                return new SubscriberCommandResult(
                    SubscriberCommandStatus.UnsupportedMode,
                    "Subscriber commands require a P25 target.");
            }

            if (!isConnected(request.SystemName))
            {
                return new SubscriberCommandResult(
                    SubscriberCommandStatus.Disconnected,
                    "FNE system is not connected.");
            }

            ISubscriberCommandTransport? transport;
            try
            {
                transport = resolveTransport(request.SystemName);
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }

            if (transport is null)
            {
                return new SubscriberCommandResult(
                    SubscriberCommandStatus.Disconnected,
                    "FNE command transport is unavailable.");
            }

            lock (ownerGate)
            {
                if (!activeOwners.Add(request.OwnerId))
                {
                    return new SubscriberCommandResult(
                        SubscriberCommandStatus.Busy,
                        "Another subscriber command is already active for this owner.");
                }
            }

            try
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCancellation.CancelAfter(timeout);

                try
                {
                    await transport.SendAsync(request, linkedCancellation.Token).ConfigureAwait(false);
                    return new SubscriberCommandResult(
                        SubscriberCommandStatus.Succeeded,
                        "Subscriber command sent.");
                }
                catch (OperationCanceledException)
                {
                    return new SubscriberCommandResult(
                        cancellationToken.IsCancellationRequested
                            ? SubscriberCommandStatus.Cancelled
                            : SubscriberCommandStatus.TimedOut,
                        cancellationToken.IsCancellationRequested
                            ? "Subscriber command cancelled."
                            : "Subscriber command timed out.");
                }
                catch (Exception exception)
                {
                    return Failed(exception);
                }
            }
            finally
            {
                lock (ownerGate)
                    activeOwners.Remove(request.OwnerId);
            }
        }

        private static SubscriberCommandResult? Validate(SubscriberCommandRequest request)
        {
            if (request is null
                || string.IsNullOrWhiteSpace(request.OwnerId)
                || string.IsNullOrWhiteSpace(request.SystemName)
                || !IsPositiveUInt(request.SourceId)
                || !IsPositiveUInt(request.DestinationId))
            {
                return new SubscriberCommandResult(
                    SubscriberCommandStatus.InvalidRequest,
                    "Subscriber command requires a system and numeric source/destination IDs.");
            }

            return null;
        }

        private static bool IsPositiveUInt(string value)
            => uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
                && parsed != 0;

        private static SubscriberCommandResult Failed(Exception exception)
            => new SubscriberCommandResult(
                SubscriberCommandStatus.Failed,
                $"Subscriber command failed: {exception.Message}");
    }
}
