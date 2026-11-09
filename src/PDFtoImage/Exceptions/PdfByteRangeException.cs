using System;

namespace PDFtoImage.Exceptions
{
    /// <summary>
    /// Thrown when a ranged PDF source cannot satisfy a read.
    /// </summary>
    public class PdfByteRangeException : Exception
    {
        /// <summary>Creates an exception with the given message.</summary>
        public PdfByteRangeException(string message) : base(message)
        {
        }

        /// <summary>Creates an exception with the given message and inner exception.</summary>
        public PdfByteRangeException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Thrown when fetched bytes would exceed <see cref="PdfByteRangeOptions.MaxFetchBytes"/>.
    /// </summary>
    public sealed class PdfByteRangeFetchLimitException : PdfByteRangeException
    {
        /// <summary>Maximum allowed fetch size in bytes.</summary>
        public long MaxFetchBytes { get; }

        /// <summary>Bytes already fetched before the limit was hit.</summary>
        public long BytesFetched { get; }

        /// <summary>Creates a fetch-limit exception.</summary>
        public PdfByteRangeFetchLimitException(long maxFetchBytes, long bytesFetched)
            : base($"PDF range fetch exceeded the limit of {maxFetchBytes} bytes (already fetched {bytesFetched} bytes).")
        {
            MaxFetchBytes = maxFetchBytes;
            BytesFetched = bytesFetched;
        }
    }
}
