using System;

namespace PDFtoImage.Exceptions
{
    /// <summary>
    /// Thrown by the optional <see cref="PdfHttpByteRangeProvider"/> when an HTTP Range (or fallback) request fails.
    /// </summary>
    public class PdfHttpRangeException : PdfByteRangeException
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
}
