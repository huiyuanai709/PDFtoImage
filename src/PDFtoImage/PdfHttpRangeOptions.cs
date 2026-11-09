using System;

namespace PDFtoImage
{
    /// <summary>
    /// Options for opening a remote PDF with HTTP Range requests so only needed byte ranges are downloaded.
    /// </summary>
    public sealed class PdfHttpRangeOptions
    {
        /// <summary>
        /// Number of bytes to fetch from the start of the file during open (header / linearized dictionary).
        /// Default is 64 KiB.
        /// </summary>
        public int HeadProbeBytes { get; set; } = 64 * 1024;

        /// <summary>
        /// Number of bytes to fetch from the end of the file when the PDF does not look linearized
        /// (trailer / <c>startxref</c>). Default is 256 KiB.
        /// </summary>
        public int TailProbeBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Minimum size of each Range request when filling a cache miss. Small PDFium reads are coalesced
        /// into at least this many bytes to reduce round-trips. Default is 64 KiB.
        /// </summary>
        public int MinRangeRequestBytes { get; set; } = 64 * 1024;

        /// <summary>
        /// Optional hard cap on total downloaded bytes. When exceeded, opening or reading throws
        /// <see cref="Exceptions.PdfHttpRangeDownloadLimitException"/>.
        /// </summary>
        public long? MaxDownloadBytes { get; set; }

        /// <summary>
        /// When the server does not advertise byte ranges, download the entire PDF into memory if
        /// <see langword="true"/>; otherwise throw. Default is <see langword="true"/>.
        /// </summary>
        public bool AllowFullDownloadFallback { get; set; } = true;

        /// <summary>
        /// Always probe the file tail during open, even when the head looks linearized.
        /// Default is <see langword="false"/>.
        /// </summary>
        public bool AlwaysProbeTail { get; set; }

        internal void Validate()
        {
            if (HeadProbeBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(HeadProbeBytes), "HeadProbeBytes must be at least 1024.");

            if (TailProbeBytes < 1024)
                throw new ArgumentOutOfRangeException(nameof(TailProbeBytes), "TailProbeBytes must be at least 1024.");

            if (MinRangeRequestBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(MinRangeRequestBytes), "MinRangeRequestBytes must be positive.");

            if (MaxDownloadBytes is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxDownloadBytes), "MaxDownloadBytes must be positive when set.");
        }
    }
}
