namespace PDFtoImage
{
    /// <summary>
    /// PDFium 2D renderer used to fill CPU bitmaps.
    /// The choice is process-wide and is fixed by the first PDFium call.
    /// <see cref="Skia"/> is the experimental <c>PDF_USE_SKIA</c> backend writing the same
    /// BGRA or Gray8 buffer as <see cref="Agg"/>. It is not a GPU surface.
    /// </summary>
    public enum PdfRenderBackend
    {
        /// <summary>Anti-Grain Geometry. This is the renderer in the stock PDFium binaries.</summary>
        Agg = 0,

        /// <summary>
        /// Skia inside PDFium. Requires a build that exports <c>FPDF_RenderPageSkia</c>.
        /// Missing that export falls back to <see cref="Agg"/>.
        /// </summary>
        Skia = 1,
    }
}
