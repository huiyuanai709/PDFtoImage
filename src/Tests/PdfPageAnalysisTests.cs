using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PDFtoImage.Tests
{
    [TestClass]
    public sealed class PdfPageAnalysisTests : TestBase
    {
        private const string Latin = "The quick brown fox jumps over the lazy dog 0123456789.";

        [TestMethod]
        public void TextPageExtractsReadingOrderWithoutImages()
        {
            var pdf = BuildTextPdf(Latin);
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);

            var analysis = session.AnalyzePage(0);
            var text = session.GetText(0);
            var stats = session.GetContentStats(0);

            AssertSame(analysis, text, stats);
            StringAssert.Contains(analysis.Text.Text, "quick brown fox");
            Assert.AreEqual(0, analysis.Text.UnknownCharacterCount);
            Assert.IsGreaterThan(0, analysis.Text.CharacterCount);
            Assert.AreEqual(analysis.Text.CharacterCount, analysis.Text.Text.Length);
            Assert.IsGreaterThan(0, analysis.Content.TextObjectCount);
            Assert.AreEqual(0, analysis.Content.InvisibleTextObjectCount);
            Assert.IsFalse(analysis.Content.TextObjectsAreInvisible);
            Assert.IsLessThan(0.05, analysis.Content.ImageAreaCoverage);
        }

        [TestMethod]
        public void ScannedImagePageHasNoTextAndCoversThePage()
        {
            var pdf = BuildScanPdf(includeText: false, invisible: false);
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
            var analysis = session.AnalyzePage(0);

            Assert.IsTrue(string.IsNullOrWhiteSpace(analysis.Text.Text), analysis.Text.Text);
            Assert.AreEqual(0, analysis.Text.UnknownCharacterCount);
            Assert.AreEqual(0, analysis.Content.TextObjectCount);
            Assert.IsFalse(analysis.Content.TextObjectsAreInvisible);
            Assert.IsGreaterThan(0.8, analysis.Content.ImageAreaCoverage, $"coverage {analysis.Content.ImageAreaCoverage}");
        }

        [TestMethod]
        public void InvisibleOcrLayerOverScanIsClassifiedAsHiddenText()
        {
            var pdf = BuildScanPdf(includeText: true, invisible: true, literal: "Hidden OCR layer 123");
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
            var analysis = session.AnalyzePage(0);

            StringAssert.Contains(analysis.Text.Text, "Hidden OCR layer");
            Assert.AreEqual(0, analysis.Text.UnknownCharacterCount);
            Assert.AreEqual(analysis.Text.CharacterCount, analysis.Text.Text.Length);
            Assert.IsGreaterThan(0, analysis.Content.TextObjectCount);
            Assert.AreEqual(analysis.Content.TextObjectCount, analysis.Content.InvisibleTextObjectCount);
            Assert.IsTrue(analysis.Content.TextObjectsAreInvisible);
            Assert.IsGreaterThan(0.8, analysis.Content.ImageAreaCoverage, $"coverage {analysis.Content.ImageAreaCoverage}");
        }

        [TestMethod]
        public void VisibleTextOverScanIsNotAnInvisibleLayer()
        {
            var pdf = BuildScanPdf(includeText: true, invisible: false, literal: "Visible caption");
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
            var analysis = session.AnalyzePage(0);

            StringAssert.Contains(analysis.Text.Text, "Visible caption");
            Assert.IsGreaterThan(0, analysis.Content.TextObjectCount);
            Assert.AreEqual(0, analysis.Content.InvisibleTextObjectCount);
            Assert.IsFalse(analysis.Content.TextObjectsAreInvisible);
            Assert.IsGreaterThan(0.8, analysis.Content.ImageAreaCoverage);
        }

        [TestMethod]
        public void ChineseTextIsExtractedAsUnicode()
        {
            var pdf = BuildChinesePdf();
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
            var analysis = session.AnalyzePage(0);

            StringAssert.Contains(analysis.Text.Text, "你好");
            Assert.AreEqual(0, analysis.Text.UnknownCharacterCount);
            Assert.IsGreaterThan(0, analysis.Text.CharacterCount);
            Assert.IsGreaterThan(0, analysis.Content.TextObjectCount);
            Assert.IsFalse(analysis.Content.TextObjectsAreInvisible);
        }

        [TestMethod]
        public void ImageInsideAFormCountsPageSpaceCoverage()
        {
            var pdf = BuildFormImagePdf();
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);
            var analysis = session.AnalyzePage(0);

            Assert.AreEqual(0, analysis.Content.TextObjectCount);
            Assert.IsGreaterThan(0.8, analysis.Content.ImageAreaCoverage, $"coverage {analysis.Content.ImageAreaCoverage}");
        }

        [TestMethod]
        public void PageIndexSelectsThatPage()
        {
            var pdf = BuildTextPdf("alpha page text", "beta page text");
            using var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);

            Assert.AreEqual(2, session.PageCount);
            StringAssert.Contains(session.GetText(0).Text, "alpha page text");
            StringAssert.Contains(session.GetText(1).Text, "beta page text");
            Assert.IsFalse(session.GetText(0).Text.Contains("beta"));
        }

        [TestMethod]
        public void OutOfRangeAndDisposedSessionThrow()
        {
            var pdf = BuildTextPdf(Latin);
            var session = PdfSession.Open(new MemoryStream(pdf), leaveOpen: true);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.AnalyzePage(-1));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.GetText(session.PageCount));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.GetContentStats(1));

            session.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.AnalyzePage(0));
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.GetText(0));
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.GetContentStats(0));
            session.Dispose();
        }

        private static void AssertSame(PdfPageAnalysis analysis, PdfPageText text, PdfPageContentStats stats)
        {
            Assert.AreEqual(analysis.Text.Text, text.Text);
            Assert.AreEqual(analysis.Text.CharacterCount, text.CharacterCount);
            Assert.AreEqual(analysis.Text.UnknownCharacterCount, text.UnknownCharacterCount);
            Assert.AreEqual(analysis.Content.TextObjectCount, stats.TextObjectCount);
            Assert.AreEqual(analysis.Content.InvisibleTextObjectCount, stats.InvisibleTextObjectCount);
            Assert.AreEqual(analysis.Content.ImageAreaCoverage, stats.ImageAreaCoverage);
        }

        private static byte[] BuildTextPdf(params string[] lines)
        {
            using var output = new MemoryStream();
            var offsets = new List<long> { 0 };
            Write(output, "%PDF-1.4\n");

            var pageCount = lines.Length;
            var pageIds = new int[pageCount];
            var contentIds = new int[pageCount];
            var next = 3;
            for (var i = 0; i < pageCount; i++)
            {
                pageIds[i] = next++;
                contentIds[i] = next++;
            }

            StartObject(output, offsets, 1);
            Write(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            StartObject(output, offsets, 2);
            Write(output, "<< /Type /Pages /Count " + pageCount + " /Kids [");
            foreach (var id in pageIds)
                Write(output, " " + id + " 0 R");
            Write(output, " ] >>\nendobj\n");

            for (var i = 0; i < pageCount; i++)
            {
                var content = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (" + lines[i] + ") Tj ET");
                StartObject(output, offsets, pageIds[i]);
                Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents " + contentIds[i] + " 0 R >>\nendobj\n");
                StartObject(output, offsets, contentIds[i]);
                Write(output, "<< /Length " + content.Length + " >>\nstream\n");
                output.Write(content);
                Write(output, "\nendstream\nendobj\n");
            }

            FinishPdf(output, offsets);
            return output.ToArray();
        }

        private static byte[] BuildScanPdf(bool includeText, bool invisible, string literal = "")
        {
            var jpeg = GrayJpeg(64, 80);
            var components = JpegComponents(jpeg);
            var colorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB";
            var content = new StringBuilder();
            content.Append("q\n612 0 0 792 0 0 cm\n/Im0 Do\nQ\n");
            if (includeText)
            {
                content.Append("BT /F1 12 Tf ");
                if (invisible)
                    content.Append("3 Tr ");
                content.Append("72 700 Td (");
                content.Append(literal);
                content.Append(") Tj ET");
            }

            var contentBytes = Encoding.ASCII.GetBytes(content.ToString());
            using var output = new MemoryStream();
            var offsets = new List<long> { 0 };
            Write(output, "%PDF-1.4\n");

            StartObject(output, offsets, 1);
            Write(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            StartObject(output, offsets, 2);
            Write(output, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");

            var font = includeText
                ? " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>"
                : string.Empty;
            StartObject(output, offsets, 3);
            Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 5 0 R >>" + font + " >> /Contents 4 0 R >>\nendobj\n");

            StartObject(output, offsets, 4);
            Write(output, "<< /Length " + contentBytes.Length + " >>\nstream\n");
            output.Write(contentBytes);
            Write(output, "\nendstream\nendobj\n");

            StartObject(output, offsets, 5);
            Write(output, "<< /Type /XObject /Subtype /Image /Width 64 /Height 80 /ColorSpace " + colorSpace + " /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
            output.Write(jpeg);
            Write(output, "\nendstream\nendobj\n");

            FinishPdf(output, offsets);
            return output.ToArray();
        }

        private static byte[] BuildFormImagePdf()
        {
            var jpeg = GrayJpeg(32, 32);
            var components = JpegComponents(jpeg);
            var colorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB";
            var pageContent = Encoding.ASCII.GetBytes("/Fm0 Do\n");
            var formContent = Encoding.ASCII.GetBytes("q\n612 0 0 792 0 0 cm\n/Im0 Do\nQ\n");

            using var output = new MemoryStream();
            var offsets = new List<long> { 0 };
            Write(output, "%PDF-1.4\n");

            StartObject(output, offsets, 1);
            Write(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            StartObject(output, offsets, 2);
            Write(output, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");
            StartObject(output, offsets, 3);
            Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
            StartObject(output, offsets, 4);
            Write(output, "<< /Length " + pageContent.Length + " >>\nstream\n");
            output.Write(pageContent);
            Write(output, "\nendstream\nendobj\n");
            StartObject(output, offsets, 5);
            Write(output, "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /XObject << /Im0 6 0 R >> >> /Length " + formContent.Length + " >>\nstream\n");
            output.Write(formContent);
            Write(output, "\nendstream\nendobj\n");
            StartObject(output, offsets, 6);
            Write(output, "<< /Type /XObject /Subtype /Image /Width 32 /Height 32 /ColorSpace " + colorSpace + " /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
            output.Write(jpeg);
            Write(output, "\nendstream\nendobj\n");

            FinishPdf(output, offsets);
            return output.ToArray();
        }

        private static byte[] BuildChinesePdf()
        {
            const string cmap =
                "/CIDInit /ProcSet findresource begin\n" +
                "12 dict begin\n" +
                "begincmap\n" +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
                "/CMapName /Adobe-Identity-UCS def\n" +
                "/CMapType 2 def\n" +
                "1 begincodespacerange\n" +
                "<0000> <FFFF>\n" +
                "endcodespacerange\n" +
                "2 beginbfchar\n" +
                "<0001> <4F60>\n" +
                "<0002> <597D>\n" +
                "endbfchar\n" +
                "endcmap\n" +
                "CMapName currentdict /CMap defineresource pop\n" +
                "end\n" +
                "end\n";
            var cmapBytes = Encoding.ASCII.GetBytes(cmap);
            var content = Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 700 Td <00010002> Tj ET");

            using var output = new MemoryStream();
            var offsets = new List<long> { 0 };
            Write(output, "%PDF-1.4\n");

            StartObject(output, offsets, 1);
            Write(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            StartObject(output, offsets, 2);
            Write(output, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n");
            StartObject(output, offsets, 3);
            Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
            StartObject(output, offsets, 4);
            Write(output, "<< /Length " + content.Length + " >>\nstream\n");
            output.Write(content);
            Write(output, "\nendstream\nendobj\n");
            StartObject(output, offsets, 5);
            Write(output, "<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>\nendobj\n");
            StartObject(output, offsets, 6);
            Write(output, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 8 0 R /DW 1000 /CIDToGIDMap /Identity >>\nendobj\n");
            StartObject(output, offsets, 7);
            Write(output, "<< /Length " + cmapBytes.Length + " >>\nstream\n");
            output.Write(cmapBytes);
            Write(output, "\nendstream\nendobj\n");
            StartObject(output, offsets, 8);
            Write(output, "<< /Type /FontDescriptor /FontName /STSong-Light /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>\nendobj\n");

            FinishPdf(output, offsets);
            return output.ToArray();
        }

        private static byte[] GrayJpeg(int width, int height)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            var pixels = bitmap.GetPixelSpan();
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(40 + (i * 17 % 180));

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 70);
            return data.ToArray();
        }

        private static int JpegComponents(byte[] jpeg)
        {
            for (var i = 0; i < jpeg.Length - 8; i++)
            {
                if (jpeg[i] != 0xFF)
                    continue;

                var marker = jpeg[i + 1];
                if (marker is 0xC0 or 0xC1 or 0xC2)
                    return jpeg[i + 9];
            }

            return 3;
        }

        private static void StartObject(Stream output, List<long> offsets, int id)
        {
            if (offsets.Count != id)
                throw new InvalidOperationException($"Object {id} written out of order.");

            offsets.Add(output.Position);
            Write(output, id + " 0 obj\n");
        }

        private static void FinishPdf(Stream output, List<long> offsets)
        {
            var xref = output.Position;
            Write(output, "xref\n0 " + offsets.Count + "\n");
            Write(output, "0000000000 65535 f \n");
            for (var i = 1; i < offsets.Count; i++)
                Write(output, offsets[i].ToString("D10") + " 00000 n \n");
            Write(output, "trailer << /Size " + offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        }

        private static void Write(Stream output, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            output.Write(bytes);
        }
    }
}
