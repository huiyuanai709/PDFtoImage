# ![PDFtoImage Logo](https://raw.githubusercontent.com/sungaila/PDFtoImage/master/etc/Icon_64.png) PDFtoImage

[![NuGet version](https://img.shields.io/nuget/v/PDFtoImage.svg?style=flat-square&logo=nuget&logoColor=white)](https://www.nuget.org/packages/PDFtoImage/)
[![NuGet downloads](https://img.shields.io/nuget/dt/PDFtoImage.svg?style=flat-square&logo=nuget&logoColor=white)](https://www.nuget.org/packages/PDFtoImage/)
[![Website](https://img.shields.io/website?up_message=online&down_message=offline&url=https%3A%2F%2Fwww.sungaila.de%2FPDFtoImage%2F&style=flat-square&label=website)](https://www.sungaila.de/PDFtoImage/)
[![GitHub license](https://img.shields.io/github/license/sungaila/PDFtoImage?style=flat-square)](https://github.com/sungaila/PDFtoImage/blob/master/LICENSE)

A .NET library for rendering PDF files as images.

PDFtoImage uses [PDFium](https://pdfium.googlesource.com/pdfium/) for rendering and [SkiaSharp](https://github.com/mono/SkiaSharp) for images.

## Getting started
Render the first page of a PDF as PNG with `PDFtoImage.Conversion`:

```csharp
using var pdf = File.OpenRead("document.pdf");

PDFtoImage.Conversion.SavePng(
    imageFilename: "page1.png",
    pdfStream: pdf,
    page: 0);
```

`SaveJpeg`, `SavePng`, `SaveWebp`, and `ToImage` render a single page. `ToImages` and `ToImagesAsync` render multiple pages. `PdfSession` keeps a document open so later pages skip another parse. `PdfSession.RenderPixels` returns packed BGRA, or 8-bit gray when `Grayscale` is set and tiling is not. `NativeGrayscale` renders that gray buffer in PDFium instead of packing a BGRA image; `ToImage` ignores the flag. Dispose `PdfPixels` when you are done with the buffer.

Dispose returned `SKBitmap` instances after use. To save one, use [`SKBitmap.Encode`](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skbitmap.encode?view=skiasharp).

### 1. Stream + page range (primary)
For local files or any seekable stream you already have. Zero-based page indices; only those pages are rendered.

```csharp
using var pdf = File.OpenRead("document.pdf");

// First 10 pages
foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdf, pages: 0..10, leaveOpen: true))
    bitmap.Dispose();

foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdf, firstPage: 0, pageCount: 10, leaveOpen: true))
    bitmap.Dispose();

using var session = PDFtoImage.PdfSession.Open(pdf, leaveOpen: true);
foreach (var bitmap in session.RenderPages(0..10))
    bitmap.Dispose();
```

### 2. URL + HTTP Range + page range
Optional helper: probes head (and tail when not linearized), fetches sparse byte ranges, then renders only the requested pages. Not required when you already have a `Stream`.

```csharp
var images = await PDFtoImage.PdfHttpByteRangeProvider.ToImagesAsync(
    new Uri("https://example.com/large.pdf"),
    pages: 0..10);

foreach (var bitmap in images)
    bitmap.Dispose();

// Or open a session and render later
using var session = await PDFtoImage.PdfHttpByteRangeProvider.OpenSessionAsync(
    "https://example.com/large.pdf");
foreach (var bitmap in session.RenderPages(firstPage: 0, pageCount: 10))
    bitmap.Dispose();
```

### 3. Custom random access (`IPdfRandomAccess`)
Implement `Read(offset, buffer, …)` yourself (S3, encrypted store, your own HTTP client, …), wrap with `PdfByteRangeStream`, then use the same page-range APIs.

```csharp
var access = PDFtoImage.PdfRandomAccess.Create(contentLength, (offset, buffer, bufferOffset, count) =>
{
    yourClient.ReadRange(offset, count, buffer, bufferOffset);
});

using var bytes = await PDFtoImage.PdfByteRangeStream.OpenAsync(access);
using var session = PDFtoImage.PdfSession.Open(bytes, leaveOpen: true);
foreach (var bitmap in session.RenderPages(0..10))
    bitmap.Dispose();
```

#### 中文摘要
1. **主 API**：`Stream` + 页范围（`0..10` / `firstPage`+`pageCount`）。  
2. **URL 辅助**：`PdfHttpByteRangeProvider`（HTTP Range + 头/尾探测）并可带页范围。  
3. **自定义后端**：`IPdfRandomAccess`。

### Unity project installation
1. Open your project and navigate to `Window` → `Package Management` → `Package Manager`.
1. Click on the `+` button (top-left corner) and select `Install package from git URL...`.
1. Enter the following URL and confirm with the `Install` button:

```
https://github.com/sungaila/PDFtoImage.git?path=etc/UnityPackage
```

## Supported runtimes
* [.NET](https://learn.microsoft.com/en-us/dotnet/core/introduction)
* [.NET Framework](https://learn.microsoft.com/en-us/dotnet/framework/get-started/overview)
* [Mono](https://www.mono-project.com/)

## Tested and supported frameworks
* [ASP.NET](https://learn.microsoft.com/en-us/aspnet/overview)
* [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/introduction-to-aspnet-core)
* [Blazor WebAssembly](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly)
* [.NET Multi-platform App UI (.NET MAUI)](https://learn.microsoft.com/en-us/dotnet/maui/what-is-maui) (excluding iOS, see [#141](https://github.com/sungaila/PDFtoImage/issues/141))
* [Unity](https://docs.unity3d.com/Manual/Mono.html) (excluding iOS, see [#141](https://github.com/sungaila/PDFtoImage/issues/141))
* [Universal Windows Platform (UWP)](https://learn.microsoft.com/en-us/windows/uwp/get-started/universal-application-platform-guide)
* [Windows UI Library 3 (WinUI 3)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)

## Parallel rendering
PDFium is not thread-safe, so PDFtoImage serializes PDFium calls within each process. For parallel rendering on .NET 11 or later, use [PDFtoImage.Parallel](https://www.nuget.org/packages/PDFtoImage.Parallel/). It runs PDFium in separate worker processes.

See the [Parallel README](https://github.com/sungaila/PDFtoImage/blob/master/src/Parallel/README.md) for examples.

## Index and Range for .NET Framework
[PolySharp](https://github.com/Sergio0694/PolySharp) provides `System.Index` and `System.Range` for .NET Framework projects. It also exposes the following generated types; avoid using them directly:

- `System.Index`
- `System.Range`
- `System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute`
- `System.Diagnostics.CodeAnalysis.NotNullWhenAttribute`
- `System.Runtime.CompilerServices.IsExternalInit`