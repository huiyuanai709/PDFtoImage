using PDFtoImage.Internals;
using System;

namespace PDFtoImage
{
    /// <summary>
    /// Process-wide switch between PDFium's AGG and Skia CPU renderers.
    /// Call <see cref="Select"/> before the first PDF operation. After that the choice is fixed
    /// because PDFium reads <c>FPDF_LIBRARY_CONFIG.m_RendererType</c> only inside
    /// <c>FPDF_InitLibraryWithConfig</c>. Parallel workers are separate processes: set
    /// <see cref="EnvironmentVariable"/> before they start, they do not see a <see cref="Select"/>
    /// call made in the parent.
    /// Skia fills the existing <c>FPDF_RenderPageBitmap</c> target (BGRA or 8-bit gray).
    /// <c>FPDF_RenderPageSkia</c> is used only as a probe that the loaded library was built with
    /// <c>PDF_USE_SKIA</c>. Passing a SkiaSharp <c>SkCanvas</c> into that export is not safe:
    /// <c>libSkiaSharp</c> is a different Skia than the one linked into PDFium. A GPU surface is a
    /// later step and needs an <c>SkCanvas</c> from that same Skia.
    /// </summary>
    public static class PdfRenderExperiment
    {
        /// <summary>Environment variable read when <see cref="Select"/> was not called. <c>agg</c> (default) or <c>skia</c>.</summary>
        public const string EnvironmentVariable = "PDFTOIMAGE_RENDERER";

        private const string MissingSkiaExport =
            "FPDF_RenderPageSkia is not exported. This PDFium was built without PDF_USE_SKIA, so rendering stays on AGG.";

#if NET9_0_OR_GREATER
        private static readonly System.Threading.Lock Gate = new();
#else
        private static readonly object Gate = new();
#endif

        private static bool _explicit;
        private static bool _prepared;
        private static bool _probed;
        private static bool _nativeInitialized;
        private static bool _skiaBuildPresent;
        private static PdfRenderBackend _requested = PdfRenderBackend.Agg;
        private static PdfRenderBackend _actual = PdfRenderBackend.Agg;
        private static string? _fallbackReason;

        /// <summary>Gets the backend requested by <see cref="Select"/> or <see cref="EnvironmentVariable"/>.</summary>
        public static PdfRenderBackend Requested
        {
            get { lock (Gate) return _requested; }
        }

        /// <summary>Gets the backend that will actually initialize PDFium. Skia falls back to AGG when the export is missing.</summary>
        public static PdfRenderBackend Actual
        {
            get { lock (Gate) return _actual; }
        }

        /// <summary>Gets whether the loaded pdfium exports <c>FPDF_RenderPageSkia</c>. False until probed.</summary>
        public static bool SkiaBuildPresent
        {
            get { lock (Gate) return _skiaBuildPresent; }
        }

        /// <summary>Gets why <see cref="Actual"/> differs from <see cref="Requested"/>, or <see langword="null"/>.</summary>
        public static string? FallbackReason
        {
            get { lock (Gate) return _fallbackReason; }
        }

        /// <summary>
        /// Maps <paramref name="value"/> to a backend. Empty, <c>agg</c>, and unknown values are AGG.
        /// <c>skia</c> is the only Skia spelling.
        /// </summary>
        public static PdfRenderBackend Parse(string? value)
        {
            if (string.Equals(value?.Trim(), "skia", StringComparison.OrdinalIgnoreCase))
                return PdfRenderBackend.Skia;

            return PdfRenderBackend.Agg;
        }

        /// <summary>Picks the backend that can actually run. Skia without the export is AGG.</summary>
        public static PdfRenderBackend Choose(PdfRenderBackend requested, bool skiaBuildPresent) =>
            requested == PdfRenderBackend.Skia && !skiaBuildPresent
                ? PdfRenderBackend.Agg
                : requested;

        /// <summary>Reason string for <see cref="Choose"/>, or <see langword="null"/> when the request can run.</summary>
        public static string? FallbackReasonFor(PdfRenderBackend requested, bool skiaBuildPresent) =>
            requested == PdfRenderBackend.Skia && !skiaBuildPresent ? MissingSkiaExport : null;

        /// <summary>
        /// Loads pdfium and reports whether <c>FPDF_RenderPageSkia</c> is exported.
        /// Does not initialize the library and does not change <see cref="Requested"/>.
        /// </summary>
        public static bool ProbeSkiaBuild()
        {
            lock (Gate)
                return ProbeUnlocked();
        }

        /// <summary>
        /// Requests a backend. Must run before the first PDF call in this process.
        /// A Skia request on an AGG-only binary is recorded as AGG and <see cref="FallbackReason"/> is set.
        /// Does not pass <c>FPDF_RENDERERTYPE_SKIA</c> into an AGG-only build: that call crashes inside PDFium.
        /// </summary>
        public static void Select(PdfRenderBackend backend)
        {
            lock (Gate)
            {
                if (_nativeInitialized)
                    throw new InvalidOperationException(
                        "PDFium is already initialized. Set " + EnvironmentVariable + " or call Select before the first PDF operation.");

                _explicit = true;
                _requested = backend;
                _skiaBuildPresent = ProbeUnlocked();
                ApplyChoiceUnlocked();
                _prepared = true;
            }
        }

        internal static void InitializeNative()
        {
            lock (Gate)
            {
                if (_nativeInitialized)
                    return;

                if (!_prepared)
                {
                    if (!_explicit)
                        _requested = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

                    _skiaBuildPresent = ProbeUnlocked();
                    ApplyChoiceUnlocked();
                    _prepared = true;
                }

                var actual = _actual;
                var fallback = _fallbackReason;

                if (fallback != null)
                    Console.Error.WriteLine("PDFtoImage: " + fallback);

                if (actual == PdfRenderBackend.Skia)
                    NativeMethods.InitLibraryWithRenderer((int)PdfRenderBackend.Skia);
                else
                    NativeMethods.InitLibrary();

                _nativeInitialized = true;
            }
        }

        private static void ApplyChoiceUnlocked()
        {
            _actual = Choose(_requested, _skiaBuildPresent);
            _fallbackReason = FallbackReasonFor(_requested, _skiaBuildPresent);
        }

        private static bool ProbeUnlocked()
        {
            if (_probed)
                return _skiaBuildPresent;

            _skiaBuildPresent = NativeMethods.HasSkiaRenderExport();
            _probed = true;
            return _skiaBuildPresent;
        }
    }
}
