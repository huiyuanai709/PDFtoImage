using System;

namespace PDFtoImage
{
    /// <summary>
    /// Options for <see cref="PdfByteRangeStream"/>: head/tail probing and sparse-cache coalescing.
    /// Transport-agnostic; HTTP-specific knobs live on <see cref="PdfHttpByteRangeProvider"/>.
    /// </summary>
    public sealed class PdfByteRangeOptions
    {
        /// <summary>
        /// Bytes to fetch from the start during open (header / linearized dictionary). Default 64 KiB.
        /// </summary>
        public int HeadProbeBytes { get; set; } = 64 * 1024;

        /// <summary>
        /// Bytes to fetch from the end when the PDF does not look linearized. Default 256 KiB.
        /// </summary>
        public int TailProbeBytes { get; set; } = 256 * 1024;

        /// <summary>
        /// Minimum size of each provider read when filling a cache miss. Default 64 KiB.
        /// </summary>
        public int MinRangeRequestBytes { get; set; } = 64 * 1024;

        /// <summary>
        /// Optional hard cap on total bytes fetched through the provider.
        /// </summary>
        public long? MaxFetchBytes { get; set; }

        /// <summary>
        /// Always probe the file tail during open, even when the head looks linearized.
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

            if (MaxFetchBytes is <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxFetchBytes), "MaxFetchBytes must be positive when set.");
        }
    }
}
