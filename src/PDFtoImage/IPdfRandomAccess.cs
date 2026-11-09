using System;

namespace PDFtoImage
{
    /// <summary>
    /// Supplies absolute byte ranges of a PDF on demand (FPDF_FILEACCESS-style).
    /// The caller owns how bytes are obtained (HTTP Range, blob store, encrypted volume, etc.).
    /// </summary>
    public interface IPdfRandomAccess
    {
        /// <summary>Total PDF size in bytes.</summary>
        long Length { get; }

        /// <summary>
        /// Copies <paramref name="count"/> bytes starting at absolute <paramref name="offset"/>
        /// into <paramref name="buffer"/> at <paramref name="bufferOffset"/>.
        /// Must fill the requested window or throw.
        /// </summary>
        /// <remarks>
        /// Invoked synchronously from PDFium's file-access path. Implementations that need async I/O
        /// should block on the reader thread or prefetch into a cache before opening the document.
        /// </remarks>
        void Read(long offset, byte[] buffer, int bufferOffset, int count);
    }

    /// <summary>
    /// Helpers for building an <see cref="IPdfRandomAccess"/> from a callback or an existing stream.
    /// </summary>
    public static class PdfRandomAccess
    {
        /// <summary>
        /// Creates a provider that forwards each range request to <paramref name="read"/>.
        /// </summary>
        public static IPdfRandomAccess Create(long length, PdfRandomAccessReadCallback read)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (read == null)
                throw new ArgumentNullException(nameof(read));

            return new CallbackPdfRandomAccess(length, read);
        }

        /// <summary>
        /// Wraps a seekable readable stream. Each range seek+reads on the caller's stream
        /// (useful when the stream already implements remote Range under the hood).
        /// </summary>
        public static IPdfRandomAccess FromStream(System.IO.Stream stream, bool leaveOpen = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead)
                throw new ArgumentException("The stream must be readable.", nameof(stream));
            if (!stream.CanSeek)
                throw new ArgumentException("The stream must be seekable.", nameof(stream));

            return new StreamPdfRandomAccess(stream, leaveOpen);
        }

        private sealed class CallbackPdfRandomAccess(long length, PdfRandomAccessReadCallback read) : IPdfRandomAccess
        {
            public long Length { get; } = length;

            public void Read(long offset, byte[] buffer, int bufferOffset, int count)
                => read(offset, buffer, bufferOffset, count);
        }

        private sealed class StreamPdfRandomAccess(System.IO.Stream stream, bool leaveOpen) : IPdfRandomAccess, IDisposable
        {
            public long Length => stream.Length;

            public void Read(long offset, byte[] buffer, int bufferOffset, int count)
            {
                stream.Position = offset;
                var total = 0;
                while (total < count)
                {
                    var n = stream.Read(buffer, bufferOffset + total, count - total);
                    if (n <= 0)
                        throw new System.IO.EndOfStreamException($"Could not read {count} bytes at offset {offset}.");
                    total += n;
                }
            }

            public void Dispose()
            {
                if (!leaveOpen)
                    stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="count"/> bytes at absolute <paramref name="offset"/> into
    /// <paramref name="buffer"/> starting at <paramref name="bufferOffset"/>.
    /// </summary>
    public delegate void PdfRandomAccessReadCallback(long offset, byte[] buffer, int bufferOffset, int count);
}
