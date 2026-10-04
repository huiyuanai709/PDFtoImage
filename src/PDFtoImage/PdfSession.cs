using PDFtoImage.Internals;
using SkiaSharp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PDFtoImage
{
    /// <summary>
    /// A PDF kept open so later pages can be rendered without parsing the file again.
    /// </summary>
    /// <remarks>
    /// PDFium is not thread-safe. Calls on every <see cref="PdfSession"/> in the process share one lock,
    /// so opening one document and rendering its pages on a single thread is the fast in-process path.
    /// Use <c>PDFtoImage.Parallel</c> when several cores should render at the same time.
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
    public sealed class PdfSession : IDisposable
    {
        private readonly PdfDocument _document;
        private bool _disposed;

        private PdfSession(PdfDocument document) => _document = document;

        /// <summary>
        /// Opens a seekable PDF stream and keeps the parsed document until <see cref="Dispose"/> is called.
        /// </summary>
        /// <param name="pdfStream">The PDF as a stream.</param>
        /// <param name="leaveOpen"><see langword="true"/> to leave <paramref name="pdfStream"/> open; otherwise it is disposed with this session.</param>
        /// <param name="password">The password for opening the PDF. Use <see langword="null"/> if no password is needed.</param>
        public static PdfSession Open(Stream pdfStream, bool leaveOpen = false, string? password = null)
        {
            if (pdfStream == null)
                throw new ArgumentNullException(nameof(pdfStream));

            return new PdfSession(PdfDocument.Load(pdfStream, password, !leaveOpen));
        }

        /// <summary>Gets the number of pages in the opened PDF.</summary>
        public int PageCount
        {
            get
            {
                ThrowIfDisposed();
                return _document.PageSizes.Count;
            }
        }

        /// <summary>
        /// Renders one page. The caller owns the returned bitmap and must dispose it.
        /// </summary>
        /// <param name="page">The zero-based page number.</param>
        /// <param name="options">Render options. The default renders at 300 DPI.</param>
        public SKBitmap Render(int page, RenderOptions options = default)
        {
            ThrowIfDisposed();
            EnsurePageInRange(page);
            return Conversion.ToImagesImpl(_document, options, [page]).First();
        }

        /// <summary>
        /// Renders the selected pages from the already opened document, in the given order.
        /// The caller owns each returned bitmap and must dispose it.
        /// </summary>
        /// <param name="pages">Zero-based page numbers. Invalid pages throw before the first image is produced.</param>
        /// <param name="options">Render options. The default renders at 300 DPI.</param>
        public IEnumerable<SKBitmap> RenderPages(IEnumerable<int> pages, RenderOptions options = default)
        {
            ThrowIfDisposed();

            if (pages == null)
                throw new ArgumentNullException(nameof(pages));

            var validated = pages.ToArray();
            var pageCount = _document.PageSizes.Count;

            if (validated.Any(page => page < 0 || page >= pageCount))
                throw new ArgumentOutOfRangeException(nameof(pages), $"The page numbers must be between 0 and {pageCount - 1}. The PDF has {pageCount} pages in total.");

            return RenderValidated(validated, options);
        }

        /// <summary>
        /// Renders one page into a packed pixel buffer.
        /// <see cref="RenderOptions.Grayscale"/> without <see cref="RenderOptions.UseTiling"/> produces 8-bit gray
        /// (<see cref="SKColorType.Gray8"/>). Each gray byte is the blue channel of the matching
        /// <see cref="Render"/> bitmap, and for a grayscale render the red and green channels are the same value.
        /// Every other combination produces premultiplied BGRA.
        /// </summary>
        /// <param name="page">The zero-based page number.</param>
        /// <param name="options">Render options. The default renders at 300 DPI.</param>
        public PdfPixels RenderPixels(int page, RenderOptions options = default)
        {
            ThrowIfDisposed();
            EnsurePageInRange(page);

            if (options == default)
                options = new RenderOptions();

            return RenderBuffer(page, options, options.Grayscale && !options.UseTiling);
        }

        /// <summary>
        /// Extracts one page's text in reading order. The document stays open.
        /// </summary>
        /// <param name="page">The zero-based page number.</param>
        public PdfPageText GetText(int page) => Inspect(page).Text;

        /// <summary>
        /// Counts text and image objects on one page. The document stays open.
        /// </summary>
        /// <param name="page">The zero-based page number.</param>
        public PdfPageContentStats GetContentStats(int page) => Inspect(page).Content;

        /// <summary>
        /// Extracts text and content statistics from a single load of the page.
        /// </summary>
        /// <param name="page">The zero-based page number.</param>
        public PdfPageAnalysis AnalyzePage(int page)
        {
            var result = Inspect(page);
            return new PdfPageAnalysis(result.Text, result.Content);
        }

        /// <summary>Closes the PDF and releases native resources.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _document.Dispose();
        }

        private PdfPageAnalysis Inspect(int page)
        {
            ThrowIfDisposed();
            EnsurePageInRange(page);
            var result = _document.InspectPage(page);
            return new PdfPageAnalysis(
                new PdfPageText(result.Text, result.CharacterCount, result.UnknownCharacterCount),
                new PdfPageContentStats(result.TextObjectCount, result.InvisibleTextObjectCount, result.ImageAreaCoverage));
        }

        private IEnumerable<SKBitmap> RenderValidated(int[] pages, RenderOptions options)
        {
            foreach (var bitmap in Conversion.ToImagesImpl(_document, options, pages))
            {
                ThrowIfDisposed();
                yield return bitmap;
            }
        }

        private PdfPixels RenderBuffer(int page, RenderOptions options, bool gray)
        {
            byte[]? pixels = null;
            byte[]? rented = null;
            var width = 0;
            var height = 0;
            var rowBytes = 0;
            var pin = default(GCHandle);

            try
            {
                _document.Render(page, options, (renderWidth, renderHeight) =>
                {
                    width = renderWidth;
                    height = renderHeight;
                    rowBytes = checked(renderWidth * 4);
                    var byteCount = checked(rowBytes * renderHeight);

                    if (gray)
                    {
                        rented = ArrayPool<byte>.Shared.Rent(byteCount);
                        pixels = rented;
                    }
                    else
                    {
                        pixels = new byte[byteCount];
                    }

                    pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    return (pin.AddrOfPinnedObject(), rowBytes);
                });

                if (!gray)
                {
                    return new PdfPixels(pixels!, width, height, rowBytes, SKColorType.Bgra8888, SKAlphaType.Premul);
                }

                var grayStride = GrayPixels.Stride(width);
                var packed = new byte[checked(grayStride * height)];

                unsafe
                {
                    fixed (byte* destination = packed)
                    {
                        if (!GrayPixels.TryPack((byte*)pin.AddrOfPinnedObject(), rowBytes, destination, grayStride, width, height))
                            throw new InvalidOperationException("Grayscale rendering did not produce opaque gray pixels.");
                    }
                }

                return new PdfPixels(packed, width, height, grayStride, SKColorType.Gray8, SKAlphaType.Opaque);
            }
            finally
            {
                if (pin.IsAllocated)
                    pin.Free();

                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private void EnsurePageInRange(int page)
        {
            var pageCount = _document.PageSizes.Count;

            if (page < 0 || page >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(page), $"The page number must be between 0 and {pageCount - 1}. The PDF has {pageCount} pages in total.");
        }

        private void ThrowIfDisposed()
        {
#if NET6_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
            if (_disposed)
                throw new ObjectDisposedException(nameof(PdfSession));
#endif
        }
    }

    /// <summary>
    /// Packed pixels for one rendered page. Dispose releases the managed buffer for collection;
    /// the buffer itself is a managed array.
    /// </summary>
    public sealed class PdfPixels : IDisposable
    {
        private readonly byte[] _pixels;
        private bool _disposed;

        internal PdfPixels(byte[] pixels, int width, int height, int rowBytes, SKColorType colorType, SKAlphaType alphaType)
        {
            _pixels = pixels;
            Width = width;
            Height = height;
            RowBytes = rowBytes;
            ColorType = colorType;
            AlphaType = alphaType;
        }

        /// <summary>Gets the width in pixels.</summary>
        public int Width { get; }

        /// <summary>Gets the height in pixels.</summary>
        public int Height { get; }

        /// <summary>Gets the number of bytes from one row to the next. Gray rows are padded to a multiple of 4.</summary>
        public int RowBytes { get; }

        /// <summary>Gets <see cref="SKColorType.Gray8"/> or <see cref="SKColorType.Bgra8888"/>.</summary>
        public SKColorType ColorType { get; }

        /// <summary>Gets <see cref="SKAlphaType.Opaque"/> for gray output and <see cref="SKAlphaType.Premul"/> for BGRA.</summary>
        public SKAlphaType AlphaType { get; }

        /// <summary>Gets <see cref="RowBytes"/> multiplied by <see cref="Height"/>.</summary>
        public int ByteCount => checked(RowBytes * Height);

        /// <summary>Gets the packed pixels. The memory is invalidated by <see cref="Dispose"/>.</summary>
        public ReadOnlyMemory<byte> Pixels
        {
            get
            {
#if NET6_0_OR_GREATER
                ObjectDisposedException.ThrowIf(_disposed, this);
#else
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PdfPixels));
#endif
                return new ReadOnlyMemory<byte>(_pixels, 0, ByteCount);
            }
        }

        /// <summary>Marks the buffer as unused. The underlying array becomes eligible for collection.</summary>
        public void Dispose() => _disposed = true;
    }
}
