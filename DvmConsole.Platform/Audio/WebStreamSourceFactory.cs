// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Net.Http;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Default web-stream source factory. The factory owns the shared HTTP
    /// transport; each created source owns only its request/decoder lifecycle.
    /// </summary>
    public sealed class WebStreamSourceFactory : IWebStreamSourceFactory, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly object _stateGate = new();
        private int _disposed;

        public WebStreamSourceFactory(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _ownsHttpClient = httpClient is null;
        }

        public IWebStreamSource Create(WebStreamSourceOptions options)
        {
            lock (_stateGate)
            {
                if (_disposed != 0)
                    throw new ObjectDisposedException(nameof(WebStreamSourceFactory));

                return new WebStreamSource(options, _httpClient);
            }
        }

        public void Dispose()
        {
            lock (_stateGate)
            {
                if (_disposed != 0)
                    return;

                _disposed = 1;
                if (_ownsHttpClient)
                    _httpClient.Dispose();
            }
        }
    }
}
