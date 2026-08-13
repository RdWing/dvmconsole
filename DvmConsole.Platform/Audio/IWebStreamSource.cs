// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Platform web-stream source contract. The source emits normalized
    /// AudioPcm.Console PCM and owns no UI/session presentation state.
    /// The <paramref name="onPcm"/> callback is invoked synchronously on the
    /// decoder worker thread: consumers must consume or copy the memory before
    /// returning, and must not synchronously await <see cref="StopAsync"/> or
    /// <see cref="IAsyncDisposable.DisposeAsync"/> from inside the callback.
    /// </summary>
    public interface IWebStreamSource : IAsyncDisposable
    {
        /// <summary>
        /// Starts the source and synchronously delivers each decoded PCM block
        /// through <paramref name="onPcm"/>.
        /// </summary>
        /// <param name="onPcm">Synchronous, borrowed-memory PCM callback.</param>
        /// <param name="cancellationToken">Token that stops connection and read work.</param>
        Task<WebStreamSourceResult> StartAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            CancellationToken cancellationToken);

        /// <summary>
        /// Starts the source and reports connection progress without exposing
        /// transport or credential details to the consumer.
        /// </summary>
        Task<WebStreamSourceResult> StartAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            CancellationToken cancellationToken,
            Action<WebStreamSourceProgress>? onProgress);

        /// <summary>Stops the source and joins its worker when called externally.</summary>
        Task StopAsync();
    }

    /// <summary>
    /// Creates web-stream sources over a shared HttpClient transport.
    /// </summary>
    public interface IWebStreamSourceFactory : IDisposable
    {
        IWebStreamSource Create(WebStreamSourceOptions options);
    }
}
