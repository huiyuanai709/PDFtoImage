# ![PDFtoImage Logo](https://raw.githubusercontent.com/sungaila/PDFtoImage/master/etc/Icon_64.png) PDFtoImage

[![GitHub Workflow Build Status](https://img.shields.io/github/actions/workflow/status/sungaila/PDFtoImage/dotnet.yml?event=push&style=flat-square&logo=github&logoColor=white)](https://github.com/sungaila/PDFtoImage/actions/workflows/dotnet.yml)
[![GitHub Workflow Test Runs Succeeded](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fgist.githubusercontent.com%2Fsungaila%2F003e8ab2211221897e4b3c0e564ed7b6%2Fraw&query=%24.stats.runs_succ&suffix=%20passed&style=flat-square&logo=github&logoColor=white&label=tests&color=45cc11)](https://github.com/sungaila/PDFtoImage/actions/workflows/dotnet.yml)
[![SonarCloud Quality Gate](https://img.shields.io/sonar/quality_gate/sungaila_PDFtoImage?server=https%3A%2F%2Fsonarcloud.io&style=flat-square&logo=sonarcloud&logoColor=white)](https://sonarcloud.io/dashboard?id=sungaila_PDFtoImage)
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

`SaveJpeg`, `SavePng`, `SaveWebp`, and `ToImage` render a single page. `ToImages` and `ToImagesAsync` render multiple pages.

Dispose returned `SKBitmap` instances after use. To save one, use [`SKBitmap.Encode`](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skbitmap.encode?view=skiasharp).

### Many pages from one PDF
`ToImages` already parses the file once and renders every requested page from that document. `PdfSession` keeps the document open across calls, which avoids paying that parse again for `GetPageCount` plus a later render:

```csharp
using var session = PDFtoImage.PdfSession.Open(File.OpenRead("document.pdf"));

for (var page = 0; page < session.PageCount; page++)
{
    using var pixels = session.RenderPixels(page, new PDFtoImage.RenderOptions(
        Dpi: 120,
        AntiAliasing: PDFtoImage.PdfAntiAliasing.None,
        Grayscale: true));
    // Grayscale without tiling returns packed Gray8 bytes (one byte per pixel, rows padded to 4).
    // Those bytes match the BGRA grayscale image; alpha is omitted because it is 255.
}
```

`AnalyzePage` reads that same open document once per page and returns the text layer plus image coverage, without rendering. `GetText` and `GetContentStats` are the same inspection split into two calls. A full-page image with no text, or with only invisible text (rendering mode 3, the usual hidden OCR layer), is a scan:

```csharp
var analysis = session.AnalyzePage(page);
var needsOcr = analysis.Content.TextObjectsAreInvisible
    || string.IsNullOrWhiteSpace(analysis.Text.Text);
```

PDFium calls in a process take one shared lock. Extra threads calling `ToImages` on copies of the same PDF do not render faster, and each call parses the file again. Use one `PdfSession` on a single thread, or [PDFtoImage.Parallel](src/Parallel/README.md) when the work should use several cores. For a large PDF, `ProcessorTransferMode.MemoryMappedFile` keeps one shared file instead of copying the PDF into every worker. Opaque grayscale pages are transferred as 8-bit gray and expanded back to the same BGRA bitmap in the host.

### Experimental Skia CPU renderer

Stock `bblanchon.PDFium` builds do not export `FPDF_RenderPageSkia` (`PDF_USE_SKIA` is off). The default renderer stays AGG, which is what `FPDF_RenderPageBitmap` uses today.

```csharp
// Before the first PDF call in this process.
PDFtoImage.PdfRenderExperiment.Select(PDFtoImage.PdfRenderBackend.Skia);
```

Or set `PDFTOIMAGE_RENDERER=skia` (default `agg`). The choice is process-wide. Parallel workers read the environment variable themselves; a `Select` call in the parent does not cross the process boundary.

When the export exists, initialization uses `FPDF_InitLibraryWithConfig` version 4 with `FPDF_RENDERERTYPE_SKIA`. Pages still land in the same CPU bitmap: BGRA, or 8-bit gray when `NativeGrayscale` is set. When the export is missing, `Actual` stays AGG, `FallbackReason` explains why, and rendering does not change. Passing Skia into an AGG-only binary crashes inside PDFium, so that call is not made.

This is not a GPU surface. `FPDF_RenderPageSkia` wants an `SkCanvas` from the Skia linked into that PDFium, not SkiaSharp's `libSkiaSharp`. A GPU-backed canvas is a later step.

Compare AGG and Skia by running the same pages twice, once with `PDFTOIMAGE_RENDERER=agg` and once with `skia`, and diff the Gray8 buffers or the OCR text. One process cannot switch after the first PDF call. Expect small antialiasing and hinting differences; do not require byte-identical pages.

The Linux x64 library that actually exports `FPDF_RenderPageSkia` is not on NuGet. [`etc/pdfium-skia/build-linux-x64.sh`](etc/pdfium-skia/build-linux-x64.sh) builds PDFium `chromium/8066` (same revision as `bblanchon.PDFium` 156.0.8066) with `pdf_use_skia` and `pdf_use_agg`, and writes one `libpdfium.so` to drop in beside the app. Chrome's in-process PDF API, Edge WebView2, and headless Chrome screenshots do not replace that library; [`etc/pdfium-skia/README.md`](etc/pdfium-skia/README.md) says why.

The same recipe is also a GitHub Actions job, [PDFium Skia linux-x64](.github/workflows/pdfium-skia-linux.yml). On a public repository the artifact is `libpdfium-skia-linux-x64`. Details and runner limits are in [`etc/pdfium-skia/README.md`](etc/pdfium-skia/README.md).

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
PDFium is not thread-safe, so PDFtoImage serializes PDFium calls within each process. For parallel rendering on .NET 11 or later, use [PDFtoImage.Parallel](https://www.nuget.org/packages/PDFtoImage.Parallel/). It runs PDFium in separate worker processes; see the [Parallel README](src/Parallel/README.md) for examples.

## Index and Range for .NET Framework
[PolySharp](https://github.com/Sergio0694/PolySharp) provides `System.Index` and `System.Range` for .NET Framework projects. It also exposes the following generated types; avoid using them directly:

- `System.Index`
- `System.Range`
- `System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute`
- `System.Diagnostics.CodeAnalysis.NotNullWhenAttribute`
- `System.Runtime.CompilerServices.IsExternalInit`