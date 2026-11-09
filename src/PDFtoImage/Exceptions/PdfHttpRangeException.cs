using System;

namespace PDFtoImage.Exceptions
{
    /// <summary>
    /// Thrown when a remote PDF cannot be opened or read via HTTP Range requests.
    /// </summary>
    public class PdfHttpRangeException : Exception
    {
        /// <summary>Creates an exception with the given message.</summary>
        public PdfHttpRangeException(string message) : base(message)
        {
        }

        /// <summary>Creates an exception with the given message and inner exception.</summary>
        public PdfHttpRangeException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Thrown when downloaded bytes would exceed <see cref="PdfHttpRangeOptions.MaxDownloadBytes"/>.
    /// </summary>
    public sealed class PdfHttpRangeDownloadLimitException : PdfHttpRangeException
    {
        /// <summary>Maximum allowed download size in bytes.</summary>
        public long MaxDownloadBytes { get; }

        /// <summary>Bytes already downloaded before the limit was hit.</summary>
        public long BytesDownloaded { get; }

        /// <summary>Creates a download-limit exception.</summary>
        public PdfHttpRangeDownloadLimitException(long maxDownloadBytes, long bytesDownloaded)
            : base($"HTTP Range download exceeded the limit of {maxDownloadBytes} bytes (already downloaded {bytesDownloaded} bytes).")
        {
            MaxDownloadBytes = maxDownloadBytes;
            BytesDownloaded = bytesDownloaded;
        }
    }
}
