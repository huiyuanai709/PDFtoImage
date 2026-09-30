using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using static PDFtoImage.Tests.TestUtils;

namespace PDFtoImage.Tests
{
    [TestClass]
    public sealed class PdfSessionTests : TestBase
    {
        private static string AssetPath => Path.Combine(AppContext.BaseDirectory, "..", "Assets", "SocialPreview.pdf");

        [TestMethod]
        public void OpenOnceMatchesRepeatedToImage()
        {
            var bytes = File.ReadAllBytes(AssetPath);
            var options = new RenderOptions(Dpi: 72, AntiAliasing: PdfAntiAliasing.None, Grayscale: true);

            using var session = PdfSession.Open(new MemoryStream(bytes), leaveOpen: true);
            using var countStream = new MemoryStream(bytes);
            Assert.AreEqual(Conversion.GetPageCount(countStream, leaveOpen: true), session.PageCount);

            using var expected = Conversion.ToImage(new MemoryStream(bytes), leaveOpen: true, options: options);
            using var actual = session.Render(0, options);
            AssertBitmapsEqual(expected, actual);

            using var pixels = session.RenderPixels(0, options);
            Assert.AreEqual(SKColorType.Gray8, pixels.ColorType);
            Assert.AreEqual(SKAlphaType.Opaque, pixels.AlphaType);
            Assert.AreEqual(expected.Width, pixels.Width);
            Assert.AreEqual(expected.Height, pixels.Height);
            Assert.IsGreaterThanOrEqualTo(pixels.Width, pixels.RowBytes == 0 ? 0 : pixels.Width);
            Assert.AreEqual(0, pixels.RowBytes % 4);
            AssertGrayMatchesBgra(expected, pixels);

            var rotated = new RenderOptions(Dpi: 40, Rotation: PdfRotation.Rotate90, Grayscale: true);
            using var rotatedExpected = Conversion.ToImage(new MemoryStream(bytes), leaveOpen: true, options: rotated);
            using var rotatedPixels = session.RenderPixels(0, rotated);
            AssertGrayMatchesBgra(rotatedExpected, rotatedPixels);
        }

        [TestMethod]
        public void ColorPixelsMatchBgraBitmap()
        {
            var bytes = File.ReadAllBytes(AssetPath);
            var options = new RenderOptions(Dpi: 36, AntiAliasing: PdfAntiAliasing.None);

            using var session = PdfSession.Open(new MemoryStream(bytes), leaveOpen: true);
            using var bitmap = session.Render(0, options);
            using var pixels = session.RenderPixels(0, options);

            Assert.AreEqual(SKColorType.Bgra8888, pixels.ColorType);
            Assert.AreEqual(SKAlphaType.Premul, pixels.AlphaType);
            Assert.AreEqual(bitmap.Width * 4, pixels.RowBytes);

            var packed = pixels.Pixels.Span;
            var native = bitmap.GetPixelSpan();

            for (var y = 0; y < bitmap.Height; y++)
            {
                var packedRow = packed.Slice(y * pixels.RowBytes, pixels.RowBytes);
                var nativeRow = native.Slice(y * bitmap.RowBytes, pixels.RowBytes);
                Assert.IsTrue(packedRow.SequenceEqual(nativeRow), $"Row {y} differs.");
            }
        }

        [TestMethod]
        public void ExposedAndHiddenMemoryStreamsMatchFile()
        {
            var bytes = File.ReadAllBytes(AssetPath);
            var options = new RenderOptions(Dpi: 48, AntiAliasing: PdfAntiAliasing.None, Grayscale: true);
            using var fromFile = RenderFile(bytes, options);
            using var exposed = Conversion.ToImage(new MemoryStream(bytes), leaveOpen: true, options: options);
            using var hidden = Conversion.ToImage(new MemoryStream(bytes, 0, bytes.Length, writable: false), leaveOpen: true, options: options);
            using var partial = Conversion.ToImage(new PartialReadStream(bytes, 128), leaveOpen: true, options: options);

            AssertBitmapsEqual(fromFile, exposed);
            AssertBitmapsEqual(fromFile, hidden);
            AssertBitmapsEqual(fromFile, partial);
        }

        [TestMethod]
        public void RenderPagesRejectsUnknownPageBeforeRendering()
        {
            using var session = PdfSession.Open(File.OpenRead(AssetPath), leaveOpen: false);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.RenderPages([-1, 0]).ToList());
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.Render(session.PageCount));
        }

        [TestMethod]
        public void DisposePreventsFurtherRendering()
        {
            var session = PdfSession.Open(File.OpenRead(AssetPath), leaveOpen: false);
            session.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.Render(0, new RenderOptions(Dpi: 36)));
            session.Dispose();
        }

        [TestMethod]
        public void FormFillMatchesSingleShotRender()
        {
            var bytes = File.ReadAllBytes(AssetPath);
            var options = new RenderOptions(Dpi: 36, WithFormFill: true);
            using var session = PdfSession.Open(new MemoryStream(bytes), leaveOpen: true);
            using var expected = Conversion.ToImage(new MemoryStream(bytes), leaveOpen: true, options: options);
            using var actual = session.Render(0, options);
            AssertBitmapsEqual(expected, actual);
        }

        private static SKBitmap RenderFile(byte[] bytes, RenderOptions options)
        {
            var path = Path.Combine(Path.GetTempPath(), "PDFtoImage.SessionTests." + Guid.NewGuid().ToString("N") + ".pdf");
            File.WriteAllBytes(path, bytes);

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Conversion.ToImage(stream, leaveOpen: true, options: options);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void AssertGrayMatchesBgra(SKBitmap bgra, PdfPixels gray)
        {
            var source = bgra.GetPixelSpan();
            var packed = gray.Pixels.Span;

            for (var y = 0; y < bgra.Height; y++)
            {
                for (var x = 0; x < bgra.Width; x++)
                {
                    var offset = (y * bgra.RowBytes) + (x * 4);
                    var pixel = packed[(y * gray.RowBytes) + x];
                    Assert.AreEqual(source[offset], pixel, $"B channel differs at {x},{y}.");
                    Assert.AreEqual(source[offset + 1], pixel, $"G channel differs at {x},{y}.");
                    Assert.AreEqual(source[offset + 2], pixel, $"R channel differs at {x},{y}.");
                }
            }
        }

        private sealed class PartialReadStream(byte[] buffer, int maxReadSize) : MemoryStream(buffer, writable: false)
        {
            public override int Read(byte[] buffer, int offset, int count)
                => base.Read(buffer, offset, Math.Min(count, maxReadSize));
        }
    }
}
