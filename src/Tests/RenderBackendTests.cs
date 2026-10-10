using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using System;
using System.IO;
using static PDFtoImage.Tests.TestUtils;

namespace PDFtoImage.Tests
{
    [TestClass]
    public class RenderBackendTests : TestBase
    {
        [TestMethod]
        [DataRow(null, PdfRenderBackend.Agg)]
        [DataRow("", PdfRenderBackend.Agg)]
        [DataRow("agg", PdfRenderBackend.Agg)]
        [DataRow("AGG", PdfRenderBackend.Agg)]
        [DataRow("nope", PdfRenderBackend.Agg)]
        [DataRow("skia", PdfRenderBackend.Skia)]
        [DataRow(" Skia ", PdfRenderBackend.Skia)]
        public void ParseRecognizesSkiaOnly(string? value, PdfRenderBackend expected)
        {
            Assert.AreEqual(expected, PdfRenderExperiment.Parse(value));
        }

        [TestMethod]
        public void MissingSkiaExportChoosesAgg()
        {
            Assert.AreEqual(PdfRenderBackend.Agg, PdfRenderExperiment.Choose(PdfRenderBackend.Skia, skiaBuildPresent: false));
            Assert.AreEqual(PdfRenderBackend.Skia, PdfRenderExperiment.Choose(PdfRenderBackend.Skia, skiaBuildPresent: true));
            Assert.AreEqual(PdfRenderBackend.Agg, PdfRenderExperiment.Choose(PdfRenderBackend.Agg, skiaBuildPresent: true));
            Assert.IsNotNull(PdfRenderExperiment.FallbackReasonFor(PdfRenderBackend.Skia, skiaBuildPresent: false));
            Assert.IsNull(PdfRenderExperiment.FallbackReasonFor(PdfRenderBackend.Skia, skiaBuildPresent: true));
            Assert.IsNull(PdfRenderExperiment.FallbackReasonFor(PdfRenderBackend.Agg, skiaBuildPresent: false));
        }

        [TestMethod]
        public void ProbeAgreesWithTheChoice()
        {
            var present = PdfRenderExperiment.ProbeSkiaBuild();
            Assert.AreEqual(present, PdfRenderExperiment.ProbeSkiaBuild());
            Assert.AreEqual(
                present ? PdfRenderBackend.Skia : PdfRenderBackend.Agg,
                PdfRenderExperiment.Choose(PdfRenderBackend.Skia, present));
        }

        [TestMethod]
        public void RendererConfigMatchesCLayout()
        {
            Assert.AreEqual(IntPtr.Size == 8 ? 48 : 24, PDFtoImage.Internals.PdfiumRendererConfig.NativeSize);
        }

        [TestMethod]
        public void NativeGrayStillFillsACpuBitmap()
        {
            using var inputStream = GetInputStream(Path.Combine("..", "Assets", "hundesteuer-anmeldung.pdf"));
            using var session = PdfSession.Open(inputStream, leaveOpen: true);
            using var pixels = session.RenderPixels(0, new RenderOptions(Dpi: 36, Grayscale: true)
            {
                NativeGrayscale = true,
            });

            Assert.AreEqual(SKColorType.Gray8, pixels.ColorType);
            Assert.IsTrue(pixels.Width > 0);
            Assert.IsTrue(pixels.Height > 0);
            Assert.IsTrue(pixels.RowBytes >= pixels.Width);

            var span = pixels.Pixels.Span;
            Assert.IsTrue(span.Length >= pixels.RowBytes * pixels.Height);

            var dark = 0;
            var light = 0;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] < 250)
                    dark++;
                else
                    light++;
            }

            // The form has a light page and dark text. Either renderer must produce both.
            Assert.IsTrue(dark > 0, "gray bitmap has no dark pixels");
            Assert.IsTrue(light > 0, "gray bitmap has no light pixels");
            Assert.IsTrue(
                PdfRenderExperiment.Actual == PdfRenderBackend.Agg || PdfRenderExperiment.SkiaBuildPresent,
                "Skia was selected without a Skia build");
        }
    }
}
