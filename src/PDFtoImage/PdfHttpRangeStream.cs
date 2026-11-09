using PDFtoImage.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage
{
    /// <summary>
    /// A seekable stream over a remote PDF that downloads byte ranges on demand via HTTP Range requests.
    /// Pass the stream to <see cref="PdfSession.Open"/> or <see cref="Conversion"/> as usual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Open probes the file head (and the tail when the PDF does not look linearized) so PDFium can
    /// resolve the trailer / cross-reference table without downloading the entire file. Later reads
    /// fetch only the missing ranges.
    /// </para>
    /// <para>
    /// Reads are synchronous because PDFium's file-access callbacks are synchronous. Prefer
    /// <see cref="OpenAsync(Uri, HttpClient?, PdfHttpRangeOptions?, CancellationToken)"/> so the initial
    /// probes run asynchronously; subsequent cache misses use blocking HTTP calls on the reader thread.
    /// </para>
    /// </remarks>
#if NET6_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("Windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("Linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("iOS13.6")]
    [System.Runtime.Versioning.SupportedOSPlatform("MacCatalyst13.5")]
    [System.Runtime.Versioning.SupportedOSPlatform("Android31.0")]
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
#endif
    public sealed class PdfHttpRangeStream : Stream
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly Uri _uri;
        private readonly PdfHttpRangeOptions _options;
        private readonly object _gate = new();
        private readonly List<Segment> _segments = new();
        private long _position;
        private long _bytesDownloaded;
        private int _rangeRequestCount;
        private bool _disposed;
        private bool _fullDownload;

        private PdfHttpRangeStream(HttpClient httpClient, bool ownsHttpClient, Uri uri, long length, PdfHttpRangeOptions options, bool supportsRange)
        {
            _httpClient = httpClient;
            _ownsHttpClient = ownsHttpClient;
            _uri = uri;
            Length = length;
            _options = options;
            SupportsRange = supportsRange;
        }

        /// <summary>Remote PDF URL.</summary>
        public Uri Uri => _uri;

        /// <summary>Total PDF size in bytes (from Content-Length / Content-Range).</summary>
        public override long Length { get; }

        /// <summary>Bytes downloaded so far (probes + on-demand ranges, or a full fallback download).</summary>
        public long BytesDownloaded
        {
            get
            {
                lock (_gate)
                    return _bytesDownloaded;
            }
        }

        /// <summary>Number of successful HTTP Range (or full GET) responses that contributed bytes.</summary>
        public int RangeRequestCount
        {
            get
            {
                lock (_gate)
                    return _rangeRequestCount;
            }
        }

        /// <summary><see langword="true"/> when the server accepted byte ranges; otherwise a full download was used.</summary>
        public bool SupportsRange { get; }

        /// <summary>
        /// <see langword="true"/> when the head probe contained a <c>/Linearized</c> dictionary;
        /// <see langword="false"/> when it did not; <see langword="null"/> before the head was inspected.
        /// </summary>
        public bool? IsLinearized { get; private set; }

        /// <inheritdoc/>
        public override bool CanRead => !_disposed;

        /// <inheritdoc/>
        public override bool CanSeek => !_disposed;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0 || value > Length)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        /// <summary>
        /// Opens a remote PDF with HTTP Range probing (head, then tail when needed).
        /// </summary>
        /// <param name="uri">HTTP or HTTPS URL of the PDF.</param>
        /// <param name="httpClient">Optional client. When <see langword="null"/>, a dedicated client is created and disposed with the stream.</param>
        /// <param name="options">Range / probe options. When <see langword="null"/>, defaults are used.</param>
        /// <param name="cancellationToken">Cancellation token for the open probes.</param>
        public static Task<PdfHttpRangeStream> OpenAsync(
            Uri uri,
            HttpClient? httpClient = null,
            PdfHttpRangeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("The URI must be an absolute http or https URL.", nameof(uri));

            options ??= new PdfHttpRangeOptions();
            options.Validate();

            var ownsClient = httpClient == null;
            httpClient ??= CreateDefaultClient();

            return OpenCoreAsync(uri, httpClient, ownsClient, options, cancellationToken);
        }

        /// <summary>
        /// Opens a remote PDF from a string URL using default options.
        /// </summary>
        public static Task<PdfHttpRangeStream> OpenAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("The URL must not be empty.", nameof(url));

            return OpenAsync(new Uri(url, UriKind.Absolute));
        }


        /// <summary>
        /// Opens a <see cref="PdfSession"/> over a remote PDF using HTTP Range requests.
        /// The session owns the underlying stream and disposes it.
        /// </summary>
        public static async Task<PdfSession> OpenSessionAsync(
            Uri uri,
            HttpClient? httpClient = null,
            PdfHttpRangeOptions? options = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            var stream = await OpenAsync(uri, httpClient, options, cancellationToken).ConfigureAwait(false);
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
        /// Opens a <see cref="PdfSession"/> from a string URL using default options.
        /// </summary>
        public static Task<PdfSession> OpenSessionAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("The URL must not be empty.", nameof(url));

            return OpenSessionAsync(new Uri(url, UriKind.Absolute));
        }


        private static async Task<PdfHttpRangeStream> OpenCoreAsync(
            Uri uri,
            HttpClient httpClient,
            bool ownsClient,
            PdfHttpRangeOptions options,
            CancellationToken cancellationToken)
        {
            try
            {
                var (length, supportsRange) = await ProbeLengthAsync(httpClient, uri, cancellationToken).ConfigureAwait(false);

                var stream = new PdfHttpRangeStream(httpClient, ownsClient, uri, length, options, supportsRange);

                if (!supportsRange)
                {
                    if (!options.AllowFullDownloadFallback)
                        throw new PdfHttpRangeException($"The server for '{uri}' does not support HTTP Range requests, and full-download fallback is disabled.");

                    await stream.DownloadFullAsync(cancellationToken).ConfigureAwait(false);
                    return stream;
                }

                var headLen = (int)Math.Min(options.HeadProbeBytes, length);
                await stream.EnsureRangeAsync(0, headLen, cancellationToken).ConfigureAwait(false);
                stream.IsLinearized = stream.DetectLinearized(headLen);

                var needsTail = options.AlwaysProbeTail || stream.IsLinearized != true;
                if (needsTail && length > headLen)
                {
                    var tailLen = (int)Math.Min(options.TailProbeBytes, length);
                    var tailStart = length - tailLen;
                    if (tailStart < headLen)
                    {
                        tailStart = headLen;
                        tailLen = (int)(length - tailStart);
                    }

                    if (tailLen > 0)
                        await stream.EnsureRangeAsync(tailStart, tailLen, cancellationToken).ConfigureAwait(false);
                }

                return stream;
            }
            catch
            {
                if (ownsClient)
                    httpClient.Dispose();
                throw;
            }
        }

        private static async Task<(long Length, bool SupportsRange)> ProbeLengthAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
        {
            // Prefer a 1-byte Range GET: many CDNs answer Content-Range without a separate HEAD.
            using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
                using var rangeResponse = await client.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (rangeResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    var contentRange = rangeResponse.Content.Headers.ContentRange;
                    if (contentRange?.HasLength == true && contentRange.Length is long rangedLength && rangedLength > 0)
                        return (rangedLength, true);

                    if (rangeResponse.Content.Headers.ContentLength is long one && one >= 0)
                    {
                        // Server returned 206 but no total length — fall through to HEAD/GET.
                    }
                    else if (contentRange?.To is long to && contentRange.From is long from)
                    {
                        // Incomplete Content-Range; continue probing.
                        _ = from;
                        _ = to;
                    }
                }
                else if (rangeResponse.IsSuccessStatusCode)
                {
                    // Server ignored Range and returned 200 — treat as no Range support.
                    if (rangeResponse.Content.Headers.ContentLength is long full && full > 0)
                        return (full, false);
                }
            }

            using (var headRequest = new HttpRequestMessage(HttpMethod.Head, uri))
            using (var headResponse = await client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (headResponse.IsSuccessStatusCode && headResponse.Content.Headers.ContentLength is long headLength && headLength > 0)
                {
                    var accepts = headResponse.Headers.AcceptRanges;
                    var supports = false;
                    foreach (var value in accepts)
                    {
                        if (string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase))
                        {
                            supports = true;
                            break;
                        }
                    }

                    return (headLength, supports);
                }
            }

            // Last resort: full GET headers only.
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            using var getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            getResponse.EnsureSuccessStatusCode();

            if (getResponse.Content.Headers.ContentLength is long getLength && getLength > 0)
            {
                var accepts = getResponse.Headers.AcceptRanges;
                var supports = false;
                foreach (var value in accepts)
                {
                    if (string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase))
                    {
                        supports = true;
                        break;
                    }
                }

                return (getLength, supports);
            }

            throw new PdfHttpRangeException($"Could not determine the Content-Length of '{uri}'.");
        }

        private async Task DownloadFullAsync(CancellationToken cancellationToken)
        {
            EnforceDownloadLimit(Length);

            using var response = await _httpClient.GetAsync(_uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var buffer = new byte[Length];
            var memory = new MemoryStream(buffer, writable: true);
            await response.Content.CopyToAsync(memory).ConfigureAwait(false);

            if (memory.Position != Length && memory.Length != Length)
            {
                // Content-Length may have been wrong; accept what we got if non-empty.
                if (memory.Position <= 0)
                    throw new PdfHttpRangeException($"Full download of '{_uri}' returned no bytes.");
            }

            var actual = new byte[memory.Position > 0 ? memory.Position : memory.Length];
            Buffer.BlockCopy(buffer, 0, actual, 0, actual.Length);

            lock (_gate)
            {
                _segments.Clear();
                _segments.Add(new Segment(0, actual));
                _bytesDownloaded = actual.Length;
                _rangeRequestCount++;
                _fullDownload = true;
            }

            IsLinearized = DetectLinearized(Math.Min(actual.Length, 8192));
        }

        private bool DetectLinearized(int inspectLength)
        {
            lock (_gate)
            {
                var buf = new byte[Math.Min(inspectLength, 8192)];
                if (!TryCopyLocked(0, buf, 0, buf.Length))
                    return false;

                // Look for "/Linearized" in the ASCII head.
                for (var i = 0; i < buf.Length - 11; i++)
                {
                    if (buf[i] == (byte)'/' &&
                        buf[i + 1] == (byte)'L' &&
                        buf[i + 2] == (byte)'i' &&
                        buf[i + 3] == (byte)'n' &&
                        buf[i + 4] == (byte)'e' &&
                        buf[i + 5] == (byte)'a' &&
                        buf[i + 6] == (byte)'r' &&
                        buf[i + 7] == (byte)'i' &&
                        buf[i + 8] == (byte)'z' &&
                        buf[i + 9] == (byte)'e' &&
                        buf[i + 10] == (byte)'d')
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            ValidateReadArgs(buffer, offset, count);

            if (count == 0 || _position >= Length)
                return 0;

            var toRead = (int)Math.Min(count, Length - _position);
            EnsureRange(_position, toRead);

            lock (_gate)
            {
                if (!TryCopyLocked(_position, buffer, offset, toRead))
                    throw new IOException($"Failed to read {toRead} bytes at offset {_position} from the HTTP Range cache.");

                _position += toRead;
                return toRead;
            }
        }

#if NETCOREAPP || NETSTANDARD2_1
        /// <inheritdoc/>
        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();

            if (buffer.Length == 0 || _position >= Length)
                return 0;

            var toRead = (int)Math.Min(buffer.Length, Length - _position);
            EnsureRange(_position, toRead);

            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                lock (_gate)
                {
                    if (!TryCopyLocked(_position, rented, 0, toRead))
                        throw new IOException($"Failed to read {toRead} bytes at offset {_position} from the HTTP Range cache.");

                    rented.AsSpan(0, toRead).CopyTo(buffer);
                    _position += toRead;
                    return toRead;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
#endif

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();

            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (next < 0 || next > Length)
                throw new IOException("Attempted to seek outside the PDF stream.");

            _position = next;
            return _position;
        }

        /// <inheritdoc/>
        public override void Flush()
        {
        }

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (disposing && _ownsHttpClient)
                _httpClient.Dispose();

            base.Dispose(disposing);
        }

        private void EnsureRange(long offset, int count)
        {
            if (count <= 0)
                return;

            // Expand to MinRangeRequestBytes when possible to cut round-trips.
            var end = offset + count;
            var fetchStart = offset;
            var fetchEnd = end;

            if (!_fullDownload && SupportsRange)
            {
                var min = _options.MinRangeRequestBytes;
                if (fetchEnd - fetchStart < min)
                {
                    fetchEnd = Math.Min(Length, fetchStart + min);
                    if (fetchEnd - fetchStart < min && fetchStart > 0)
                        fetchStart = Math.Max(0, fetchEnd - min);
                }
            }

            // Skip work when already fully cached.
            lock (_gate)
            {
                if (IsFullyCachedLocked(fetchStart, fetchEnd - fetchStart))
                    return;
            }

            EnsureRangeAsync(fetchStart, (int)(fetchEnd - fetchStart), CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }

        private async Task EnsureRangeAsync(long offset, int count, CancellationToken cancellationToken)
        {
            if (count <= 0 || offset >= Length)
                return;

            count = (int)Math.Min(count, Length - offset);

            List<(long Start, int Length)> missing;
            lock (_gate)
            {
                if (IsFullyCachedLocked(offset, count))
                    return;

                missing = ListMissingLocked(offset, count);
            }

            foreach (var gap in missing)
            {
                await FetchRangeAsync(gap.Start, gap.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task FetchRangeAsync(long offset, int count, CancellationToken cancellationToken)
        {
            if (count <= 0)
                return;

            count = (int)Math.Min(count, Length - offset);
            EnforceDownloadLimit(_bytesDownloaded + count);

            if (!SupportsRange || _fullDownload)
            {
                // Should not happen for gaps after a full download.
                throw new PdfHttpRangeException("Cannot fetch a Range from a stream that fell back to a full download.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent && response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new PdfHttpRangeException($"HTTP Range request for '{_uri}' failed with status {(int)response.StatusCode} ({response.StatusCode}).");

            byte[] data;
#if NETCOREAPP
            data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
            data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif

            if (data.Length == 0)
                throw new PdfHttpRangeException($"HTTP Range request for '{_uri}' returned an empty body.");

            // If the server ignored Range and returned the whole file, accept it as a full download.
            if (response.StatusCode == System.Net.HttpStatusCode.OK && data.Length > count && data.Length >= Length)
            {
                lock (_gate)
                {
                    EnforceDownloadLimitLocked(data.Length);
                    _segments.Clear();
                    _segments.Add(new Segment(0, data));
                    _bytesDownloaded = data.Length;
                    _rangeRequestCount++;
                    _fullDownload = true;
                }

                return;
            }

            // Trim oversized responses to the requested window when Content-Range is absent.
            if (data.Length > count)
            {
                var trimmed = new byte[count];
                Buffer.BlockCopy(data, 0, trimmed, 0, count);
                data = trimmed;
            }

            lock (_gate)
            {
                EnforceDownloadLimitLocked(_bytesDownloaded + data.Length);
                AddSegmentLocked(offset, data);
                _bytesDownloaded += data.Length;
                _rangeRequestCount++;
            }
        }

        private void EnforceDownloadLimit(long projected)
        {
            lock (_gate)
                EnforceDownloadLimitLocked(projected);
        }

        private void EnforceDownloadLimitLocked(long projected)
        {
            if (_options.MaxDownloadBytes is long max && projected > max)
                throw new PdfHttpRangeDownloadLimitException(max, _bytesDownloaded);
        }

        private bool IsFullyCachedLocked(long offset, long count)
        {
            if (count <= 0)
                return true;

            var end = offset + count;
            var cursor = offset;

            foreach (var segment in _segments)
            {
                var segEnd = segment.Offset + segment.Data.Length;
                if (segEnd <= cursor)
                    continue;
                if (segment.Offset > cursor)
                    return false;

                cursor = Math.Max(cursor, segEnd);
                if (cursor >= end)
                    return true;
            }

            return cursor >= end;
        }

        private List<(long Start, int Length)> ListMissingLocked(long offset, int count)
        {
            var gaps = new List<(long Start, int Length)>();
            var end = offset + count;
            var cursor = offset;

            foreach (var segment in _segments)
            {
                var segEnd = segment.Offset + segment.Data.Length;
                if (segEnd <= cursor)
                    continue;

                if (segment.Offset > cursor)
                {
                    var gapEnd = Math.Min(segment.Offset, end);
                    gaps.Add((cursor, (int)(gapEnd - cursor)));
                    cursor = gapEnd;
                }

                cursor = Math.Max(cursor, Math.Min(segEnd, end));
                if (cursor >= end)
                    break;
            }

            if (cursor < end)
                gaps.Add((cursor, (int)(end - cursor)));

            return gaps;
        }

        private void AddSegmentLocked(long offset, byte[] data)
        {
            // Insert sorted and merge overlaps / adjacency.
            var newSeg = new Segment(offset, data);
            var index = _segments.FindIndex(s => s.Offset > offset);
            if (index < 0)
                _segments.Add(newSeg);
            else
                _segments.Insert(index, newSeg);

            MergeSegmentsLocked();
        }

        private void MergeSegmentsLocked()
        {
            if (_segments.Count <= 1)
                return;

            var merged = new List<Segment>(_segments.Count);
            var current = _segments[0];

            for (var i = 1; i < _segments.Count; i++)
            {
                var next = _segments[i];
                var currentEnd = current.Offset + current.Data.Length;

                if (next.Offset <= currentEnd)
                {
                    var nextEnd = next.Offset + next.Data.Length;
                    if (nextEnd <= currentEnd)
                        continue;

                    var combined = new byte[nextEnd - current.Offset];
                    Buffer.BlockCopy(current.Data, 0, combined, 0, current.Data.Length);
                    var copyFrom = (int)(currentEnd - next.Offset);
                    if (copyFrom < 0)
                        copyFrom = 0;
                    Buffer.BlockCopy(next.Data, copyFrom, combined, current.Data.Length, next.Data.Length - copyFrom);
                    current = new Segment(current.Offset, combined);
                }
                else if (next.Offset == currentEnd)
                {
                    var combined = new byte[current.Data.Length + next.Data.Length];
                    Buffer.BlockCopy(current.Data, 0, combined, 0, current.Data.Length);
                    Buffer.BlockCopy(next.Data, 0, combined, current.Data.Length, next.Data.Length);
                    current = new Segment(current.Offset, combined);
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            _segments.Clear();
            _segments.AddRange(merged);
        }

        private bool TryCopyLocked(long offset, byte[] buffer, int bufferOffset, int count)
        {
            var end = offset + count;
            var written = 0;

            foreach (var segment in _segments)
            {
                var segEnd = segment.Offset + segment.Data.Length;
                if (segEnd <= offset || segment.Offset >= end)
                    continue;

                var copyStart = Math.Max(offset, segment.Offset);
                var copyEnd = Math.Min(end, segEnd);
                var len = (int)(copyEnd - copyStart);
                var srcIndex = (int)(copyStart - segment.Offset);
                var dstIndex = bufferOffset + (int)(copyStart - offset);
                Buffer.BlockCopy(segment.Data, srcIndex, buffer, dstIndex, len);
                written += len;
            }

            return written == count;
        }

        private static HttpClient CreateDefaultClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PDFtoImage-HttpRange/1.0");
            return client;
        }

        private static void ValidateReadArgs(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (buffer.Length - offset < count)
                throw new ArgumentException("The buffer is too small for the requested read.");
        }

        private void ThrowIfDisposed()
        {
#if NET6_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
            if (_disposed)
                throw new ObjectDisposedException(nameof(PdfHttpRangeStream));
#endif
        }

        private readonly struct Segment
        {
            public Segment(long offset, byte[] data)
            {
                Offset = offset;
                Data = data;
            }

            public long Offset { get; }
            public byte[] Data { get; }
        }
    }
}
