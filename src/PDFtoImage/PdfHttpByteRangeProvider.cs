using PDFtoImage.Exceptions;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage
{
    /// <summary>
    /// Optional HTTP Range adapter for <see cref="IPdfRandomAccess"/>.
    /// The caller can supply any other provider instead; this is not required to use ranged PDFs.
    /// </summary>
#if NET6_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("Linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("iOS13.6")]
    [System.Runtime.Versioning.SupportedOSPlatform("MacCatalyst13.5")]
    [System.Runtime.Versioning.SupportedOSPlatform("Android31.0")]
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
#endif
    public sealed class PdfHttpByteRangeProvider : IPdfRandomAccess, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly Uri _uri;
        private readonly bool _supportsRange;
        private byte[]? _fullFallback;
        private bool _disposed;

        private PdfHttpByteRangeProvider(HttpClient httpClient, bool ownsHttpClient, Uri uri, long length, bool supportsRange)
        {
            _httpClient = httpClient;
            _ownsHttpClient = ownsHttpClient;
            _uri = uri;
            Length = length;
            _supportsRange = supportsRange;
        }

        /// <summary>Remote PDF URL.</summary>
        public Uri Uri => _uri;

        /// <inheritdoc/>
        public long Length { get; }

        /// <summary><see langword="true"/> when the server accepted byte ranges.</summary>
        public bool SupportsRange => _supportsRange;

        /// <summary>
        /// Creates an HTTP Range provider. When the server lacks Range support and
        /// <paramref name="allowFullDownloadFallback"/> is true, the whole PDF is downloaded once.
        /// </summary>
        public static async Task<PdfHttpByteRangeProvider> CreateAsync(
            Uri uri,
            HttpClient? httpClient = null,
            bool allowFullDownloadFallback = true,
            CancellationToken cancellationToken = default)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("The URI must be an absolute http or https URL.", nameof(uri));

            var ownsClient = httpClient == null;
            httpClient ??= CreateDefaultClient();

            try
            {
                var (length, supportsRange) = await ProbeLengthAsync(httpClient, uri, cancellationToken).ConfigureAwait(false);
                var provider = new PdfHttpByteRangeProvider(httpClient, ownsClient, uri, length, supportsRange);

                if (!supportsRange)
                {
                    if (!allowFullDownloadFallback)
                        throw new PdfHttpRangeException($"The server for '{uri}' does not support HTTP Range requests, and full-download fallback is disabled.");

                    await provider.DownloadFullAsync(cancellationToken).ConfigureAwait(false);
                }

                return provider;
            }
            catch
            {
                if (ownsClient)
                    httpClient.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Convenience: HTTP provider + <see cref="PdfByteRangeStream.OpenAsync"/> in one step.
        /// </summary>
        public static async Task<PdfByteRangeStream> OpenStreamAsync(
            Uri uri,
            HttpClient? httpClient = null,
            PdfByteRangeOptions? rangeOptions = null,
            bool allowFullDownloadFallback = true,
            CancellationToken cancellationToken = default)
        {
            var provider = await CreateAsync(uri, httpClient, allowFullDownloadFallback, cancellationToken).ConfigureAwait(false);
            try
            {
                return await PdfByteRangeStream.OpenAsync(provider, rangeOptions, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                provider.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens a <see cref="PdfSession"/> over a remote PDF using HTTP Range (head/tail probe + sparse fetches).
        /// </summary>
        public static async Task<PdfSession> OpenSessionAsync(
            Uri uri,
            HttpClient? httpClient = null,
            PdfByteRangeOptions? rangeOptions = null,
            bool allowFullDownloadFallback = true,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            var stream = await OpenStreamAsync(uri, httpClient, rangeOptions, allowFullDownloadFallback, cancellationToken).ConfigureAwait(false);
            try
            {
                return PdfSession.Open(stream, leaveOpen: false, password);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Downloads only the byte ranges needed, then renders the given <paramref name="pages"/> (zero-based).
        /// The caller owns and must dispose each returned bitmap.
        /// </summary>
        public static async Task<IReadOnlyList<SKBitmap>> ToImagesAsync(
            Uri uri,
            Range pages,
            HttpClient? httpClient = null,
            PdfByteRangeOptions? rangeOptions = null,
            bool allowFullDownloadFallback = true,
            string? password = null,
            RenderOptions options = default,
            CancellationToken cancellationToken = default)
        {
            using var session = await OpenSessionAsync(uri, httpClient, rangeOptions, allowFullDownloadFallback, password, cancellationToken).ConfigureAwait(false);
            var (offset, length) = pages.GetOffsetAndLength(session.PageCount);
            return session.RenderPages(Enumerable.Range(offset, length), options).ToList();
        }

        /// <summary>
        /// Downloads only the byte ranges needed, then renders <paramref name="pageCount"/> pages from <paramref name="firstPage"/>.
        /// </summary>
        public static Task<IReadOnlyList<SKBitmap>> ToImagesAsync(Uri uri, int firstPage, int pageCount)
        {
            if (firstPage < 0)
                throw new ArgumentOutOfRangeException(nameof(firstPage));
            if (pageCount < 0)
                throw new ArgumentOutOfRangeException(nameof(pageCount));

            return ToImagesAsync(uri, firstPage..(firstPage + pageCount));
        }

        /// <summary>URL string convenience for <see cref="OpenStreamAsync(Uri, HttpClient?, PdfByteRangeOptions?, bool, CancellationToken)"/>.</summary>
        public static Task<PdfByteRangeStream> OpenStreamAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("The URL must not be empty.", nameof(url));
            return OpenStreamAsync(new Uri(url, UriKind.Absolute));
        }

        /// <summary>URL string convenience for <see cref="OpenSessionAsync(Uri, HttpClient?, PdfByteRangeOptions?, bool, string?, CancellationToken)"/>.</summary>
        public static Task<PdfSession> OpenSessionAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("The URL must not be empty.", nameof(url));
            return OpenSessionAsync(new Uri(url, UriKind.Absolute));
        }

        /// <inheritdoc/>
        public void Read(long offset, byte[] buffer, int bufferOffset, int count)
        {
            ThrowIfDisposed();

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || bufferOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer.Length - bufferOffset < count)
                throw new ArgumentException("The buffer is too small for the requested read.");
            if (offset + count > Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (_fullFallback != null)
            {
                Buffer.BlockCopy(_fullFallback, (int)offset, buffer, bufferOffset, count);
                return;
            }

            if (!_supportsRange)
                throw new PdfHttpRangeException("HTTP Range is unavailable and no full-download fallback was loaded.");

            // Sync path required by IPdfRandomAccess / PDFium.
            FetchRange(offset, count, buffer, bufferOffset);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_ownsHttpClient)
                _httpClient.Dispose();
        }

        private void FetchRange(long offset, int count, byte[] buffer, int bufferOffset)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);

#if NET5_0_OR_GREATER
            using var response = _httpClient.Send(request, HttpCompletionOption.ResponseContentRead);
#else
            using var response = _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult();
#endif
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent &&
                response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new PdfHttpRangeException(
                    $"HTTP Range request for '{_uri}' failed with status {(int)response.StatusCode} ({response.StatusCode}).");
            }

            var data = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (data.Length == 0)
                throw new PdfHttpRangeException($"HTTP Range request for '{_uri}' returned an empty body.");

            if (response.StatusCode == System.Net.HttpStatusCode.OK && data.Length >= Length)
            {
                _fullFallback = data.Length == Length ? data : Trim(data, (int)Length);
                Buffer.BlockCopy(_fullFallback, (int)offset, buffer, bufferOffset, count);
                return;
            }

            if (data.Length < count)
                throw new PdfHttpRangeException($"HTTP Range request for '{_uri}' returned {data.Length} bytes, expected {count}.");

            Buffer.BlockCopy(data, 0, buffer, bufferOffset, count);
        }

        private async Task DownloadFullAsync(CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(_uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
#if NETCOREAPP
            var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
            var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
            if (data.Length == 0)
                throw new PdfHttpRangeException($"Full download of '{_uri}' returned no bytes.");

            _fullFallback = data;
        }

        private static async Task<(long Length, bool SupportsRange)> ProbeLengthAsync(
            HttpClient client, Uri uri, CancellationToken cancellationToken)
        {
            using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
                using var rangeResponse = await client.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (rangeResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    var contentRange = rangeResponse.Content.Headers.ContentRange;
                    if (contentRange?.HasLength == true && contentRange.Length is long rangedLength && rangedLength > 0)
                        return (rangedLength, true);
                }
                else if (rangeResponse.IsSuccessStatusCode &&
                         rangeResponse.Content.Headers.ContentLength is long full && full > 0)
                {
                    return (full, false);
                }
            }

            using (var headRequest = new HttpRequestMessage(HttpMethod.Head, uri))
            using (var headResponse = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (headResponse.IsSuccessStatusCode &&
                    headResponse.Content.Headers.ContentLength is long headLength && headLength > 0)
                {
                    return (headLength, HasBytesAcceptRanges(headResponse));
                }
            }

            using var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            using var getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            getResponse.EnsureSuccessStatusCode();

            if (getResponse.Content.Headers.ContentLength is long getLength && getLength > 0)
                return (getLength, HasBytesAcceptRanges(getResponse));

            throw new PdfHttpRangeException($"Could not determine the Content-Length of '{uri}'.");
        }

        private static bool HasBytesAcceptRanges(HttpResponseMessage response)
        {
            foreach (var value in response.Headers.AcceptRanges)
            {
                if (string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static byte[] Trim(byte[] data, int length)
        {
            var trimmed = new byte[length];
            Buffer.BlockCopy(data, 0, trimmed, 0, length);
            return trimmed;
        }

        private static HttpClient CreateDefaultClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PDFtoImage-HttpRange/1.0");
            return client;
        }

        private void ThrowIfDisposed()
        {
#if NET6_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
            if (_disposed)
                throw new ObjectDisposedException(nameof(PdfHttpByteRangeProvider));
#endif
        }
    }
}
