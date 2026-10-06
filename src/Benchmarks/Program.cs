using PDFtoImage;
using PDFtoImage.Parallel;
using SkiaSharp;
using System.Diagnostics;
using System.Text;

// Release benchmark for multi-page PDF rendering. Generates its own PDFs so the
// numbers do not depend on checked-in sample files.
//
//   dotnet run -c Release --project src/Benchmarks -- --scan-pages 8 --text-pages 40

var scanPages = ArgInt("--scan-pages", 8);
var textPages = ArgInt("--text-pages", 40);
var runs = ArgInt("--runs", 3);
var dpi = ArgInt("--dpi", 120);
var skipParallel = args.Contains("--skip-parallel");

var options = new RenderOptions(Dpi: dpi, AntiAliasing: PdfAntiAliasing.None, Grayscale: true);
Console.WriteLine($"OCR-style options: DPI={dpi}, AntiAliasing=None, Grayscale=true, runs={runs}");
Console.WriteLine();

if (args.Contains("--profile"))
{
    await ProfileLease(scanPages, options, runs);
    return;
}

if (args.Contains("--hotpath"))
{
    await ProfileHotPath(scanPages, options, runs);
    return;
}

var textPdf = BuildTextPdf(textPages);
var (scanPdf, scanInfo) = BuildScanPdf(scanPages);
Console.WriteLine($"Text PDF: {textPages} pages, {textPdf.Length / 1024.0:F0} KiB");
Console.WriteLine($"Scan PDF: {scanPages} pages, {scanPdf.Length / (1024.0 * 1024.0):F1} MiB ({scanInfo})");
Console.WriteLine();

ReportAnalyze("plain AnalyzePage (one line, document open)", BuildTextPdf(textPages, 1));
ReportAnalyze("dense AnalyzePage (48 lines, document open)", textPdf);
ReportAnalyze("scan AnalyzePage (document already open)", scanPdf);
Console.WriteLine();

Report("text sequential ToImages (MemoryStream)", () => RenderAll(textPdf, options));
Report("text reload every page (MemoryStream)", () => RenderReloading(textPdf, options));
Report("text 2 workers, split pages, shared byte[]", () => RenderWorkers(textPdf, options, 2));
Report("text 4 workers, split pages, shared byte[]", () => RenderWorkers(textPdf, options, 4));

Report("scan sequential ToImages (MemoryStream)", () => RenderAll(scanPdf, options));
Report("scan PdfSession.Render same document", () => RenderSession(scanPdf, options));
Report("scan PdfSession.RenderPixels Gray8", () => RenderGray(scanPdf, options));
Report("scan sequential ToImages (FileStream)", () => RenderFile(scanPdf, options));
Report("scan reload every page (MemoryStream)", () => RenderReloading(scanPdf, options));
Report("scan GetPageCount + ToImages", () =>
{
    using var stream = new MemoryStream(scanPdf);
    _ = Conversion.GetPageCount(stream, leaveOpen: true);
    stream.Position = 0;
    RenderStream(stream, options);
});
Report("scan 2 workers, split pages, shared byte[]", () => RenderWorkers(scanPdf, options, 2));
Report("scan 4 workers, split pages, shared byte[]", () => RenderWorkers(scanPdf, options, 4));

if (!skipParallel)
{
    await ReportSteadyParallel("scan Parallel IPC workers=1 steady", scanPdf, options, 1, ProcessorTransferMode.Ipc);
    await ReportSteadyParallel("scan Parallel IPC workers=4 steady", scanPdf, options, 4, ProcessorTransferMode.Ipc);
    await ReportSteadyParallel("scan Parallel MMF workers=1 steady", scanPdf, options, 1, ProcessorTransferMode.MemoryMappedFile);
    await ReportSteadyParallel("scan Parallel MMF workers=4 steady", scanPdf, options, 4, ProcessorTransferMode.MemoryMappedFile);
}

async Task ProfileLease(int pageCount, RenderOptions options, int runs)
{
    var (pdf, info) = BuildScanPdf(pageCount);
    var path = Path.Combine(Path.GetTempPath(), "PDFtoImage.Profile." + Guid.NewGuid().ToString("N") + ".pdf");
    File.WriteAllBytes(path, pdf);
    Console.WriteLine($"Profile scan PDF: {pageCount} pages, {pdf.Length / (1024.0 * 1024.0):F1} MiB ({info})");
    Console.WriteLine();
    CompareNative(Path.Combine("src", "Tests", "Assets", "SocialPreview.pdf"), options);
    CompareNative(Path.Combine("src", "Tests", "Assets", "Wikimedia_Commons_web.pdf"), options);
    CompareNative(Path.Combine("src", "Tests", "Assets", "Wikimedia_Commons_web.pdf"), options with { Rotation = PdfRotation.Rotate90 });
    CompareNative(path, options);

    void Time(string name, Action action)
    {
        action();
        var samples = new double[runs];
        for (var i = 0; i < samples.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        Console.WriteLine($"{name,-56} median {samples[samples.Length / 2],8:F2} ms   samples [{string.Join(", ", samples.Select(sample => sample.ToString("F1")))}]");
    }

    Time("session open (file)", () =>
    {
        using var stream = File.OpenRead(path);
        using var session = PdfSession.Open(stream, leaveOpen: true);
        _ = session.PageCount;
    });

    Time("session open + first Gray8 page", () =>
    {
        using var stream = File.OpenRead(path);
        using var session = PdfSession.Open(stream, leaveOpen: true);
        using var pixels = session.RenderPixels(0, options);
        if (pixels.ByteCount <= 0)
            throw new InvalidOperationException();
    });

    using (var stream = File.OpenRead(path))
    using (var session = PdfSession.Open(stream, leaveOpen: true))
    {
        using var warmup = session.RenderPixels(0, options);
        Console.WriteLine($"Gray8 page: {warmup.Width}x{warmup.Height}, {warmup.ByteCount / 1024.0:F0} KiB");
        Time("Gray8 pages 1-4, document open", () =>
        {
            for (var page = 1; page <= 4 && page < session.PageCount; page++)
                using (session.RenderPixels(page, options)) { }
        });
        Time("BGRA pages 1-4, document open", () =>
        {
            for (var page = 1; page <= 4 && page < session.PageCount; page++)
                using (session.Render(page, options)) { }
        });
        var nativeOptions = options with { NativeGrayscale = true };
        using (var exact = session.RenderPixels(1, options))
        using (var native = session.RenderPixels(1, nativeOptions))
        {
            long sum = 0;
            var max = 0;
            var count = 0;
            var left = exact.Pixels.Span;
            var right = native.Pixels.Span;
            for (var y = 0; y < exact.Height; y++)
            {
                for (var x = 0; x < exact.Width; x++)
                {
                    var delta = Math.Abs(left[y * exact.RowBytes + x] - right[y * native.RowBytes + x]);
                    sum += delta;
                    if (delta > max)
                        max = delta;
                    count++;
                }
            }

            Console.WriteLine($"Native gray vs exact gray: mean abs {sum / (double)count:F2}, max {max}, {exact.Width}x{exact.Height}");
        }
        Time("native gray pages 1-4, document open", () =>
        {
            for (var page = 1; page <= 4 && page < session.PageCount; page++)
                using (session.RenderPixels(page, nativeOptions)) { }
        });
    }

    var jpeg = GrayJpeg(1700, 2200);
    Time("Skia decode + resize JPEG to 1105x1430", () =>
    {
        using var decoded = SKBitmap.Decode(jpeg);
        using var scaled = decoded.Resize(new SKImageInfo(1105, 1430, SKColorType.Gray8, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (scaled == null || scaled.ByteCount <= 0)
            throw new InvalidOperationException();
    });

    var largePages = Math.Max(pageCount, 160);
    var (largePdf, largeInfo) = BuildScanPdf(largePages);
    var largePath = Path.Combine(Path.GetTempPath(), "PDFtoImage.Profile." + Guid.NewGuid().ToString("N") + ".pdf");
    File.WriteAllBytes(largePath, largePdf);
    Console.WriteLine($"Large scan PDF: {largePages} pages, {largePdf.Length / (1024.0 * 1024.0):F1} MiB ({largeInfo})");
    Time("large session open (file)", () =>
    {
        using var stream = File.OpenRead(largePath);
        using var session = PdfSession.Open(stream, leaveOpen: true);
        _ = session.PageCount;
    });
    File.Delete(largePath);

    Console.WriteLine();
    var lease = Math.Min(4, pageCount);
    await TimeLease("MMF one call, 8 pages, 4 workers", path, lease * 2, options, cold: false, warmupPages: lease * 2);
    await TimeLease("MMF cold processor, 4-page lease", path, lease, options, cold: true);
    await TimeLease("MMF warm process, next 4-page lease", path, lease, options, cold: false);
    await TimeLease("IPC cold processor, 4-page lease", path, lease, options, cold: true, ipc: true);
    await TimeLease("IPC warm process, next 4-page lease", path, lease, options, cold: false, ipc: true);
    await TimeLease("IPC share+retain cold, 4-page lease", path, lease, options, cold: true, ipc: true, share: true, retain: true);
    await TimeLease("IPC share+retain warm, 4-page lease", path, lease, options, cold: false, ipc: true, share: true, retain: true);
    await TimeLease("MMF retain warm, 4-page lease", path, lease, options, cold: false, retain: true);
    File.Delete(path);
}

async Task ProfileHotPath(int pageCount, RenderOptions options, int runs)
{
    var native = options with { NativeGrayscale = true };
    var (pdf, info) = BuildScanPdf(pageCount);
    var path = Path.Combine(Path.GetTempPath(), "PDFtoImage.HotPath." + Guid.NewGuid().ToString("N") + ".pdf");
    await File.WriteAllBytesAsync(path, pdf);
    Console.WriteLine($"Hot-path scan: {pageCount} pages, {pdf.Length / (1024.0 * 1024.0):F1} MiB ({info})");
    using (var stream = File.OpenRead(path))
    using (var session = PdfSession.Open(stream, leaveOpen: true))
    {
        using var first = session.RenderPixels(0, native);
        Console.WriteLine($"Gray8 {first.Width}x{first.Height} {first.ByteCount / 1024.0:F0} KiB");
        Time("expand Gray8 to BGRA", () => Expand(first));
        Time("copy Gray8 buffer", () => CopyGray(first));
    }

    var lease = Math.Min(4, pageCount);
    await TimeLease("MMF ToImages 2 workers warm 4-page", path, lease, native, cold: false, workers: 2, retain: true);
    await TimeLease("MMF ToPixels 2 workers warm 4-page", path, lease, native, cold: false, workers: 2, retain: true, pixels: true);
    await TimeLease("MMF ToImages 4 workers warm 4-page", path, lease, native, cold: false, workers: 4, retain: true);
    await TimeLease("MMF ToPixels 4 workers warm 4-page", path, lease, native, cold: false, workers: 4, retain: true, pixels: true);
    await TimeCold("cold first 4-page lease", path, lease, native);
    await TimeCold("prewarmed first 4-page lease", path, lease, native, prewarm: true);
    File.Delete(path);

    void Time(string name, Action action)
    {
        action();
        var samples = new double[runs];
        for (var i = 0; i < samples.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        Console.WriteLine($"{name,-56} median {samples[samples.Length / 2],8:F2} ms");
    }

    static void Expand(PdfPixels pixels)
    {
        var bgra = new byte[checked(pixels.Width * 4 * pixels.Height)];
        var source = pixels.Pixels.Span;
        for (var y = 0; y < pixels.Height; y++)
        {
            var row = y * pixels.RowBytes;
            var dst = y * pixels.Width * 4;
            for (var x = 0; x < pixels.Width; x++)
            {
                var gray = source[row + x];
                bgra[dst + x * 4] = gray;
                bgra[dst + x * 4 + 1] = gray;
                bgra[dst + x * 4 + 2] = gray;
                bgra[dst + x * 4 + 3] = 255;
            }
        }

        if (bgra[0] == 1 && bgra[^1] == 2)
            throw new InvalidOperationException();
    }

    static void CopyGray(PdfPixels pixels)
    {
        var copy = new byte[pixels.ByteCount];
        pixels.Pixels.Span.CopyTo(copy);
        if (copy[0] == 1 && copy[^1] == 2)
            throw new InvalidOperationException();
    }
}

static void CompareNative(string path, RenderOptions options)
{
    if (!File.Exists(path))
        return;

    var native = options with { NativeGrayscale = true };
    using var stream = File.OpenRead(path);
    using var session = PdfSession.Open(stream, leaveOpen: true);
    using var exact = session.RenderPixels(0, options);
    using var other = session.RenderPixels(0, native);
    long sum = 0;
    var max = 0;
    var changed = 0;
    var count = 0;
    var left = exact.Pixels.Span;
    var right = other.Pixels.Span;
    var height = Math.Min(exact.Height, other.Height);
    var width = Math.Min(exact.Width, other.Width);
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var delta = Math.Abs(left[y * exact.RowBytes + x] - right[y * other.RowBytes + x]);
            sum += delta;
            if (delta > max)
                max = delta;
            if (delta != 0)
                changed++;
            count++;
        }
    }

    Console.WriteLine($"native vs exact {Path.GetFileName(path)} rot={options.Rotation}: mean {sum / (double)Math.Max(count, 1):F3} max {max} changed {changed * 100.0 / Math.Max(count, 1):F2}% {exact.Width}x{exact.Height}");
}

static byte[] GrayJpeg(int width, int height)
{
    using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
    var pixels = bitmap.GetPixelSpan();
    uint state = 0xC0FFEE;
    for (var i = 0; i < pixels.Length; i++)
    {
        state = state * 1664525 + 1013904223;
        pixels[i] = (byte)(40 + (state >> 24) % 180);
    }

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 60);
    return data.ToArray();
}

async Task TimeCold(string name, string path, int pages, RenderOptions options, bool prewarm = false)
{
    var temp = Path.Combine(Path.GetTempPath(), "PDFtoImage.Profile." + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var startup = Stopwatch.StartNew();
        await using var processor = new ParallelPdfProcessor(new ProcessorOptions
        {
            WorkerCount = 2,
            TransferMode = ProcessorTransferMode.MemoryMappedFile,
            TempDirectory = temp,
            RetainDocuments = true,
            PrewarmWorkers = prewarm
        });
        if (prewarm)
            await processor.PrewarmAsync();
        startup.Stop();
        var watch = Stopwatch.StartNew();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await foreach (var pixels in processor.ToImagesPixelsAsync(stream, Enumerable.Range(0, pages).ToArray(), leaveOpen: true, options: options))
            pixels.Dispose();
        watch.Stop();
        Console.WriteLine($"{name,-56} {watch.Elapsed.TotalMilliseconds,8:F2} ms   startup {startup.Elapsed.TotalMilliseconds:F0} ms");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

async Task TimeLease(string name, string path, int pages, RenderOptions options, bool cold, bool ipc = false, int warmupPages = 0, bool share = false, bool retain = false, int workers = 4, bool pixels = false)
{
    var mode = ipc ? ProcessorTransferMode.Ipc : ProcessorTransferMode.MemoryMappedFile;
    var temp = Path.Combine(Path.GetTempPath(), "PDFtoImage.Profile." + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        await using var processor = new ParallelPdfProcessor(new ProcessorOptions
        {
            WorkerCount = workers,
            TransferMode = mode,
            TempDirectory = temp,
            ShareSourceFile = share,
            RetainDocuments = retain
        });

        async Task RenderLease(int start, int count)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var range = Enumerable.Range(start, count).ToArray();
            if (pixels)
            {
                await foreach (var image in processor.ToImagesPixelsAsync(stream, range, leaveOpen: true, options: options))
                    image.Dispose();
            }
            else
            {
                await foreach (var bitmap in processor.ToImagesAsync(stream, range, leaveOpen: true, options: options))
                    bitmap.Dispose();
            }
        }

        if (warmupPages > 0)
            await RenderLease(0, warmupPages);
        else if (!cold)
            await RenderLease(0, pages);

        var start = cold || warmupPages > 0 ? 0 : pages;
        var samples = new double[cold ? 1 : runs];
        for (var i = 0; i < samples.Length; i++)
        {
            var watch = Stopwatch.StartNew();
            await RenderLease(start, pages);
            watch.Stop();
            samples[i] = watch.Elapsed.TotalMilliseconds;
        }

        if (cold)
        {
            Console.WriteLine($"{name,-56} {samples[0],8:F2} ms");
            return;
        }

        Array.Sort(samples);
        Console.WriteLine($"{name,-56} median {samples[samples.Length / 2],8:F2} ms   samples [{string.Join(", ", samples.Select(sample => sample.ToString("F1")))}]");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

void ReportAnalyze(string name, byte[] pdf)
{
    using var stream = new MemoryStream(pdf, writable: false);
    using var session = PdfSession.Open(stream, leaveOpen: true);
    var pages = session.PageCount;

    int Analyze()
    {
        var chars = 0;
        for (var page = 0; page < pages; page++)
            chars += session.AnalyzePage(page).Text.CharacterCount;
        return chars;
    }

    var chars = Analyze();
    var samples = new double[runs];
    var allocated = new long[runs];
    for (var i = 0; i < samples.Length; i++)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        if (Analyze() < 0)
            throw new InvalidOperationException();
        watch.Stop();
        allocated[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
        samples[i] = watch.Elapsed.TotalMilliseconds;
    }

    Array.Sort(samples);
    Array.Sort(allocated);
    var median = samples[samples.Length / 2];
    var perPage = pages == 0 ? 0 : median / pages;
    var charsPerPage = pages == 0 ? 0 : chars / pages;
    Console.WriteLine($"{name,-52} median {median,8:F2} ms   {perPage,7:F3} ms/page   {charsPerPage,6} chars/page   alloc {allocated[allocated.Length / 2] / 1024.0,8:F0} KiB   samples [{string.Join(", ", samples.Select(sample => sample.ToString("F2")))}]");
}

void Report(string name, Action action)
{
    action();
    var samples = new long[runs];
    var allocated = new long[runs];
    for (var i = 0; i < samples.Length; i++)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        allocated[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
        samples[i] = watch.ElapsedMilliseconds;
    }

    Array.Sort(samples);
    Array.Sort(allocated);
    Console.WriteLine($"{name,-52} median {samples[samples.Length / 2],6} ms   alloc {allocated[allocated.Length / 2] / 1024.0,8:F0} KiB   samples [{string.Join(", ", samples)}]");
}

void RenderSession(byte[] pdf, RenderOptions options)
{
    using var stream = new MemoryStream(pdf, writable: false);
    using var session = PdfSession.Open(stream, leaveOpen: true);
    for (var page = 0; page < session.PageCount; page++)
        using (session.Render(page, options)) { }
}

void RenderGray(byte[] pdf, RenderOptions options)
{
    using var stream = new MemoryStream(pdf, writable: false);
    using var session = PdfSession.Open(stream, leaveOpen: true);
    for (var page = 0; page < session.PageCount; page++)
        using (session.RenderPixels(page, options)) { }
}

void RenderAll(byte[] pdf, RenderOptions options)
{
    using var stream = new MemoryStream(pdf);
    RenderStream(stream, options);
}

void RenderFile(byte[] pdf, RenderOptions options)
{
    var path = Path.Combine(Path.GetTempPath(), "PDFtoImage.Benchmarks." + Guid.NewGuid().ToString("N") + ".pdf");
    File.WriteAllBytes(path, pdf);
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        RenderStream(stream, options);
    }
    finally
    {
        File.Delete(path);
    }
}

void RenderStream(Stream stream, RenderOptions options)
{
    foreach (var bitmap in Conversion.ToImages(stream, leaveOpen: true, options: options))
        bitmap.Dispose();
}

void RenderReloading(byte[] pdf, RenderOptions options)
{
    using var countStream = new MemoryStream(pdf);
    var count = Conversion.GetPageCount(countStream, leaveOpen: true);
    for (var page = 0; page < count; page++)
    {
        using var stream = new MemoryStream(pdf);
        using var bitmap = Conversion.ToImage(stream, page, leaveOpen: true, options: options);
    }
}

void RenderWorkers(byte[] pdf, RenderOptions options, int workers)
{
    using var countStream = new MemoryStream(pdf);
    var count = Conversion.GetPageCount(countStream, leaveOpen: true);
    var ranges = Split(count, workers);
    Task.WaitAll(ranges.Select(range => Task.Run(() =>
    {
        using var stream = new MemoryStream(pdf, writable: false);
        foreach (var bitmap in Conversion.ToImages(stream, range, leaveOpen: true, options: options))
            bitmap.Dispose();
    })).ToArray());
}

async Task ReportSteadyParallel(string name, byte[] pdf, RenderOptions options, int workers, ProcessorTransferMode mode)
{
    var temp = Path.Combine(Path.GetTempPath(), "PDFtoImage.Benchmarks." + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        await using var processor = new ParallelPdfProcessor(new ProcessorOptions
        {
            WorkerCount = workers,
            TransferMode = mode,
            TempDirectory = temp
        });

        async Task RenderOnce()
        {
            using var stream = new MemoryStream(pdf, writable: false);
            await foreach (var bitmap in processor.ToImagesAsync(stream, leaveOpen: true, options: options))
                bitmap.Dispose();
        }

        await RenderOnce();
        var samples = new long[runs];
        var allocated = new long[runs];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            await RenderOnce();
            watch.Stop();
            allocated[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
            samples[i] = watch.ElapsedMilliseconds;
        }

        Array.Sort(samples);
        Array.Sort(allocated);
        Console.WriteLine($"{name,-52} median {samples[samples.Length / 2],6} ms   alloc {allocated[allocated.Length / 2] / 1024.0,8:F0} KiB   samples [{string.Join(", ", samples)}]");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

List<int>[] Split(int count, int workers)
{
    var ranges = Enumerable.Range(0, workers).Select(_ => new List<int>()).ToArray();
    for (var page = 0; page < count; page++)
        ranges[page % workers].Add(page);
    return ranges;
}

byte[] BuildTextPdf(int pageCount, int linesPerPage = 48)
{
    var sentence = "The quick brown fox jumps over the lazy dog 0123456789.";
    var paragraph = linesPerPage == 1 ? sentence : string.Join(" ", Enumerable.Repeat(sentence, 30));
    using var output = new MemoryStream();
    var offsets = new List<long> { 0 };
    Write(output, "%PDF-1.4\n");

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
        var content = new StringBuilder();
        content.Append("BT /F1 9 Tf 40 760 Td 14 TL ");
        for (var line = 0; line < linesPerPage; line++)
        {
            content.Append('(');
            content.Append("Page ");
            content.Append(i + 1);
            content.Append(" line ");
            content.Append(line);
            content.Append(' ');
            content.Append(paragraph);
            content.Append(") ' ");
        }

        content.Append("ET");
        var contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        StartObject(output, offsets, pageIds[i]);
        Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents " + contentIds[i] + " 0 R >>\nendobj\n");

        StartObject(output, offsets, contentIds[i]);
        Write(output, "<< /Length " + contentBytes.Length + " >>\nstream\n");
        output.Write(contentBytes);
        Write(output, "\nendstream\nendobj\n");
    }

    FinishPdf(output, offsets);
    return output.ToArray();
}

(byte[] Pdf, string Info) BuildScanPdf(int pageCount)
{
    const int width = 1700;
    const int height = 2200;
    using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
    var pixels = bitmap.GetPixelSpan();
    uint state = 0xC0FFEE;
    for (var i = 0; i < pixels.Length; i++)
    {
        state = state * 1664525 + 1013904223;
        pixels[i] = (byte)(40 + (state >> 24) % 180);
    }

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 60);
    var jpeg = data.ToArray();
    var components = JpegComponents(jpeg);

    using var output = new MemoryStream();
    var offsets = new List<long> { 0 };
    Write(output, "%PDF-1.4\n");

    var pageIds = new int[pageCount];
    var contentIds = new int[pageCount];
    var imageIds = new int[pageCount];
    var next = 3;
    for (var i = 0; i < pageCount; i++)
    {
        pageIds[i] = next++;
        contentIds[i] = next++;
        imageIds[i] = next++;
    }

    StartObject(output, offsets, 1);
    Write(output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
    StartObject(output, offsets, 2);
    Write(output, "<< /Type /Pages /Count " + pageCount + " /Kids [");
    foreach (var id in pageIds)
        Write(output, " " + id + " 0 R");
    Write(output, " ] >>\nendobj\n");

    var colorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB";
    var contentBytes = Encoding.ASCII.GetBytes("q\n612 0 0 792 0 0 cm\n/Im0 Do\nQ\n");

    for (var i = 0; i < pageCount; i++)
    {
        StartObject(output, offsets, pageIds[i]);
        Write(output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 " + imageIds[i] + " 0 R >> >> /Contents " + contentIds[i] + " 0 R >>\nendobj\n");

        StartObject(output, offsets, contentIds[i]);
        Write(output, "<< /Length " + contentBytes.Length + " >>\nstream\n");
        output.Write(contentBytes);
        Write(output, "\nendstream\nendobj\n");

        StartObject(output, offsets, imageIds[i]);
        Write(output, "<< /Type /XObject /Subtype /Image /Width " + width + " /Height " + height + " /ColorSpace " + colorSpace + " /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
        output.Write(jpeg);
        Write(output, "\nendstream\nendobj\n");
    }

    FinishPdf(output, offsets);
    return (output.ToArray(), $"{width}x{height} JPEG x{pageCount}, {jpeg.Length / 1024} KiB/page, {components} component(s)");
}

int JpegComponents(byte[] jpeg)
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

void StartObject(Stream output, List<long> offsets, int id)
{
    if (offsets.Count != id)
        throw new InvalidOperationException($"Object {id} written out of order.");
    offsets.Add(output.Position);
    Write(output, id + " 0 obj\n");
}

void FinishPdf(Stream output, List<long> offsets)
{
    var xref = output.Position;
    Write(output, "xref\n0 " + offsets.Count + "\n");
    Write(output, "0000000000 65535 f \n");
    for (var i = 1; i < offsets.Count; i++)
        Write(output, offsets[i].ToString("D10") + " 00000 n \n");
    Write(output, "trailer << /Size " + offsets.Count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
}

void Write(Stream output, string value)
{
    var bytes = Encoding.ASCII.GetBytes(value);
    output.Write(bytes);
}

int ArgInt(string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) ? value : fallback;
}
