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
    /// </summary>
    public interface IWebStreamSource : IAsyncDisposable
    {
        Task<WebStreamSourceResult> StartAsync(
            Action<ReadOnlyMemory<byte>> onPcm,
            CancellationToken cancellationToken);

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
