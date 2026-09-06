// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.Http.Headers;
using System.Text;

namespace DvmConsole.Application;

internal static class HttpWebStreamSource
{
    public static async Task<Stream> OpenAsync(
        WebStreamPlaybackDescriptor configuration,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(configuration.Url, UriKind.Absolute, out Uri? uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The web stream URL must be an absolute HTTP or HTTPS URL.");
        }

        var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(configuration.AuthUsername))
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{configuration.AuthUsername}:{configuration.AuthPassword ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Stream content = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            request.Dispose();
            return new OwnedHttpResponseStream(client, response, content);
        }
        catch
        {
            request.Dispose();
            response?.Dispose();
            client.Dispose();
            throw;
        }
    }

    private sealed class OwnedHttpResponseStream(
        HttpClient client,
        HttpResponseMessage response,
        Stream inner) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            client.Dispose();
            GC.SuppressFinalize(this);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken)
            => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
