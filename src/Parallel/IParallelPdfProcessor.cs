using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage.Parallel
{
    /// <summary>Renders PDF pages with isolated worker processes.</summary>
    // These overloads mirror the existing preview API, whose page selectors have distinct types.
    public interface IParallelPdfProcessor : IDisposable, IAsyncDisposable
    {
        /// <summary>Renders a single page.</summary>
        /// <param name="pdfStream">The PDF to render.</param>
        /// <param name="page">The page index, optionally counted from the end.</param>
        /// <param name="leaveOpen">Whether to leave <paramref name="pdfStream"/> open after it has been read.</param>
        /// <param name="password">The optional PDF password.</param>
        /// <param name="options">Rendering options.</param>
        /// <param name="cancellationToken">Cancels reading or rendering the request.</param>
        Task<SKBitmap> ToImageAsync(Stream pdfStream, Index page = default, bool leaveOpen = false, string? password = null, RenderOptions options = default, CancellationToken cancellationToken = default);

        /// <summary>Renders every page.</summary>
        IAsyncEnumerable<SKBitmap> ToImagesAsync(Stream pdfStream, bool leaveOpen = false, string? password = null, RenderOptions options = default, CancellationToken cancellationToken = default);

        /// <summary>Renders a page range.</summary>
        IAsyncEnumerable<SKBitmap> ToImagesAsync(Stream pdfStream, Range pages, bool leaveOpen = false, string? password = null, RenderOptions options = default, CancellationToken cancellationToken = default);

        /// <summary>Renders selected pages in the requested order.</summary>
        IAsyncEnumerable<SKBitmap> ToImagesAsync(Stream pdfStream, IEnumerable<int> pages, bool leaveOpen = false, string? password = null, RenderOptions options = default, CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts the worker processes and completes when they have connected.
        /// </summary>
        /// <param name="cancellationToken">Cancels waiting for startup.</param>
        Task PrewarmAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes a file PDF that <see cref="ProcessorOptions.RetainDocuments"/> kept open in worker
        /// processes, including its file handle and memory mapping. Completes after every worker that
        /// had <paramref name="path"/> loaded has acknowledged the close. Workers that are busy with
        /// that file are waited on; workers rendering a different file are left alone.
        /// </summary>
        /// <param name="path">The PDF path previously rendered from a <see cref="FileStream"/>.</param>
        /// <param name="cancellationToken">Cancels waiting. A worker already sent an unload is still read to completion.</param>
        Task ReleaseRetainedFileAsync(string path, CancellationToken cancellationToken = default);

        /// <summary>
        /// Renders one page to packed pixels. Grayscale output is 8-bit gray, not expanded to BGRA.
        /// </summary>
        Task<PdfPixels> ToPixelsAsync(Stream pdfStream, Index page = default, bool leaveOpen = false, string? password = null, RenderOptions options = default, CancellationToken cancellationToken = default);

        /// <summary>Renders selected pages to packed pixels, in the requested order.</summary>
        IAsyncEnumerable<PdfPixels> ToImagesPixelsAsync(Stream pdfStream, IEnumerable<int> pages, bool leaveOpen = false, string? password = null, RenderOptions options = default, CancellationToken cancellationToken = default);
    }
}