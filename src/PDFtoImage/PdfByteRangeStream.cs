using PDFtoImage.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage
{
    /// <summary>
    /// A seekable stream over an <see cref="IPdfRandomAccess"/>.
    /// PDFium reads go through the normal stream path; missing byte windows are fetched via
    /// <see cref="IPdfRandomAccess.Read"/> (offset + length) and cached.
    /// </summary>
    /// <remarks>
    /// Pass this stream to <see cref="PdfSession.Open"/> or <see cref="Conversion"/>. Prefer
    /// <see cref="OpenAsync"/> so head/tail probes run before the first PDFium parse.
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
    public sealed class PdfByteRangeStream : Stream
    {
        private readonly IPdfRandomAccess _provider;
        private readonly PdfByteRangeOptions _options;
        private readonly object _gate = new();
        private readonly List<Segment> _segments = new();
        private long _position;
        private long _bytesFetched;
        private int _rangeFetchCount;
        private bool _disposed;

        private PdfByteRangeStream(IPdfRandomAccess provider, PdfByteRangeOptions options)
        {
            _provider = provider;
            _options = options;
            Length = provider.Length;
        }

        /// <summary>Underlying range provider.</summary>
        public IPdfRandomAccess Provider => _provider;

        /// <inheritdoc/>
        public override long Length { get; }

        /// <summary>Total bytes fetched from the provider so far (probes + on-demand ranges).</summary>
        public long BytesFetched
        {
            get { lock (_gate) return _bytesFetched; }
        }

        /// <summary>Number of provider reads that contributed bytes.</summary>
        public int RangeFetchCount
        {
            get { lock (_gate) return _rangeFetchCount; }
        }

        /// <summary>
        /// <see langword="true"/> when the head probe contained <c>/Linearized</c>;
        /// <see langword="false"/> when it did not; <see langword="null"/> before probing.
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
        /// Opens a ranged PDF stream: probes the head (and tail when needed), then returns a seekable
        /// stream ready for <see cref="PdfSession.Open"/>.
        /// </summary>
        public static Task<PdfByteRangeStream> OpenAsync(
            IPdfRandomAccess provider,
            PdfByteRangeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            options ??= new PdfByteRangeOptions();
            options.Validate();

            if (provider.Length <= 0)
                throw new PdfByteRangeException("The PDF range provider reported a non-positive Length.");

            return OpenCoreAsync(provider, options, cancellationToken);
        }

        /// <summary>
        /// Opens a <see cref="PdfSession"/> over a ranged provider. The session owns the stream.
        /// </summary>
        public static async Task<PdfSession> OpenSessionAsync(
            IPdfRandomAccess provider,
            PdfByteRangeOptions? options = null,
            string? password = null,
            CancellationToken cancellationToken = default)
        {
            var stream = await OpenAsync(provider, options, cancellationToken).ConfigureAwait(false);
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
        /// Creates a stream without probing. Use when the caller already warmed the needed ranges,
        /// or when probes will happen naturally on first PDFium reads.
        /// </summary>
        public static PdfByteRangeStream Create(IPdfRandomAccess provider, PdfByteRangeOptions? options = null)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            options ??= new PdfByteRangeOptions();
            options.Validate();

            if (provider.Length <= 0)
                throw new PdfByteRangeException("The PDF range provider reported a non-positive Length.");

            return new PdfByteRangeStream(provider, options);
        }

        private static async Task<PdfByteRangeStream> OpenCoreAsync(
            IPdfRandomAccess provider,
            PdfByteRangeOptions options,
            CancellationToken cancellationToken)
        {
            var stream = new PdfByteRangeStream(provider, options);

            // Probes call the sync provider; run them on the thread pool so OpenAsync stays async-friendly.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = stream.Length;
                var headLen = (int)Math.Min(options.HeadProbeBytes, length);
                stream.EnsureRange(0, headLen);
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
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.EnsureRange(tailStart, tailLen);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            return stream;
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
                    throw new IOException($"Failed to read {toRead} bytes at offset {_position} from the range cache.");

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
                        throw new IOException($"Failed to read {toRead} bytes at offset {_position} from the range cache.");

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

            if (disposing && _provider is IDisposable disposable)
                disposable.Dispose();

            base.Dispose(disposing);
        }

        private void EnsureRange(long offset, int count)
        {
            if (count <= 0)
                return;

            var fetchStart = offset;
            var fetchEnd = offset + count;
            var min = _options.MinRangeRequestBytes;

            if (fetchEnd - fetchStart < min)
            {
                fetchEnd = Math.Min(Length, fetchStart + min);
                if (fetchEnd - fetchStart < min && fetchStart > 0)
                    fetchStart = Math.Max(0, fetchEnd - min);
            }

            List<(long Start, int Length)> missing;
            lock (_gate)
            {
                if (IsFullyCachedLocked(fetchStart, fetchEnd - fetchStart))
                    return;

                missing = ListMissingLocked(fetchStart, (int)(fetchEnd - fetchStart));
            }

            foreach (var gap in missing)
                FetchRange(gap.Start, gap.Length);
        }

        private void FetchRange(long offset, int count)
        {
            if (count <= 0 || offset >= Length)
                return;

            count = (int)Math.Min(count, Length - offset);

            lock (_gate)
            {
                if (IsFullyCachedLocked(offset, count))
                    return;

                EnforceFetchLimitLocked(_bytesFetched + count);
            }

            var data = new byte[count];
            try
            {
                _provider.Read(offset, data, 0, count);
            }
            catch (Exception ex) when (ex is not PdfByteRangeException)
            {
                throw new PdfByteRangeException($"The range provider failed to read {count} bytes at offset {offset}.", ex);
            }

            lock (_gate)
            {
                EnforceFetchLimitLocked(_bytesFetched + data.Length);
                AddSegmentLocked(offset, data);
                _bytesFetched += data.Length;
                _rangeFetchCount++;
            }
        }

        private bool DetectLinearized(int inspectLength)
        {
            lock (_gate)
            {
                var buf = new byte[Math.Min(inspectLength, 8192)];
                if (!TryCopyLocked(0, buf, 0, buf.Length))
                    return false;

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

        private void EnforceFetchLimitLocked(long projected)
        {
            if (_options.MaxFetchBytes is long max && projected > max)
                throw new PdfByteRangeFetchLimitException(max, _bytesFetched);
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
                var nextEnd = next.Offset + next.Data.Length;

                if (next.Offset <= currentEnd)
                {
                    if (nextEnd <= currentEnd)
                        continue;

                    var combined = new byte[nextEnd - current.Offset];
                    Buffer.BlockCopy(current.Data, 0, combined, 0, current.Data.Length);
                    var already = (int)(currentEnd - next.Offset);
                    if (already < 0)
                        already = 0;
                    Buffer.BlockCopy(next.Data, already, combined, current.Data.Length, next.Data.Length - already);
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
                throw new ObjectDisposedException(nameof(PdfByteRangeStream));
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
