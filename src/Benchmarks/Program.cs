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

var textPdf = BuildTextPdf(textPages);
var (scanPdf, scanInfo) = BuildScanPdf(scanPages);
Console.WriteLine($"Text PDF: {textPages} pages, {textPdf.Length / 1024.0:F0} KiB");
Console.WriteLine($"Scan PDF: {scanPages} pages, {scanPdf.Length / (1024.0 * 1024.0):F1} MiB ({scanInfo})");
Console.WriteLine();

ReportAnalyze("text AnalyzePage (document already open)", textPdf);
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

    _ = Analyze();
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
    Console.WriteLine($"{name,-52} median {median,8:F2} ms   {median / pages,7:F3} ms/page   alloc {allocated[allocated.Length / 2] / 1024.0,8:F0} KiB   samples [{string.Join(", ", samples.Select(sample => sample.ToString("F2")))}]");
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

byte[] BuildTextPdf(int pageCount)
{
    var paragraph = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog 0123456789.", 30));
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
        for (var line = 0; line < 48; line++)
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
