// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class WebStreamPlaybackStatePublisher(
    Func<WebStreamPlaybackState, ValueTask> observer)
{
    private readonly Func<WebStreamPlaybackState, ValueTask> observer =
        observer ?? throw new ArgumentNullException(nameof(observer));

    public ValueTask PublishAsync(
        WebStreamPlaybackDescriptor stream,
        bool active,
        bool connecting,
        bool receiving,
        bool failed,
        string status)
        => PublishAsync(stream.Id, active, connecting, receiving, failed, status);

    public async ValueTask PublishAsync(
        WebStreamId streamId,
        bool active,
        bool connecting,
        bool receiving,
        bool failed,
        string status)
    {
        try
        {
            await observer(new WebStreamPlaybackState(
                    streamId,
                    active,
                    connecting,
                    receiving,
                    failed,
                    status))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Presentation is an observer of playback ownership. A recycled
            // view or failed dispatcher callback must not turn a successful
            // start into a leaked stream or interrupt stop/disposal.
            System.Diagnostics.Trace.TraceError(
                "Web-stream playback state observer failed: {0}",
                exception);
        }
    }
}
