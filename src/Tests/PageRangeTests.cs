using Microsoft.VisualStudio.TestTools.UnitTesting;
using static PDFtoImage.Tests.TestUtils;
using System.IO;
using System.Linq;

namespace PDFtoImage.Tests
{
    [TestClass]
    public class PageRangeTests : TestBase
    {
        [TestMethod]
        public void ToImagesFirstPageCountRendersOnlyRequestedPages()
        {
            using var input = GetInputStream(Path.Combine("..", "Assets", "hundesteuer-anmeldung.pdf"));
            var bitmaps = Conversion.ToImages(input, firstPage: 0, pageCount: 1).ToList();
            try
            {
                Assert.AreEqual(1, bitmaps.Count);
                Assert.IsGreaterThan(0, bitmaps[0].ByteCount);
            }
            finally
            {
                bitmaps.ForEach(b => b.Dispose());
            }
        }

        [TestMethod]
        public void ToImagesSystemRangeRendersFirstPages()
        {
            using var input = GetInputStream(Path.Combine("..", "Assets", "hundesteuer-anmeldung.pdf"));
            var bitmaps = Conversion.ToImages(input, pages: 0..1, leaveOpen: true).ToList();
            try
            {
                Assert.AreEqual(1, bitmaps.Count);
            }
            finally
            {
                bitmaps.ForEach(b => b.Dispose());
            }
        }

        [TestMethod]
        public void PdfSessionRenderPagesRange()
        {
            using var input = GetInputStream(Path.Combine("..", "Assets", "hundesteuer-anmeldung.pdf"));
            using var session = PdfSession.Open(input, leaveOpen: true);
            var bitmaps = session.RenderPages(0..1).ToList();
            try
            {
                Assert.AreEqual(1, bitmaps.Count);
            }
            finally
            {
                bitmaps.ForEach(b => b.Dispose());
            }
        }

        [TestMethod]
        public void PdfSessionRenderPagesFirstPageCount()
        {
            using var input = GetInputStream(Path.Combine("..", "Assets", "SocialPreview.pdf"));
            using var session = PdfSession.Open(input, leaveOpen: true);
            var bitmaps = session.RenderPages(firstPage: 0, pageCount: 1).ToList();
            try
            {
                Assert.AreEqual(1, bitmaps.Count);
            }
            finally
            {
                bitmaps.ForEach(b => b.Dispose());
            }
        }
    }
}
