# Linux x64 PDFium with AGG and Skia

`bblanchon.PDFium` 156.0.8066, the package this repo references, exports `FPDF_InitLibrary`, `FPDF_InitLibraryWithConfig`, and `FPDF_RenderPageBitmap`. It does not export `FPDF_RenderPageSkia`. The maintainer declined `PDF_USE_SKIA` packages (pdfium-binaries issues 29 and 144): a Skia variant roughly doubles the build matrix, and the renderer is still experimental.

`PdfRenderExperiment` already selects the renderer at runtime. It only calls `FPDF_InitLibraryWithConfig` with `FPDF_RENDERERTYPE_SKIA` when `FPDF_RenderPageSkia` is exported. On the stock library the request stays on AGG. This directory is how to build the library that makes the Skia request real.

## What the binary is

One `libpdfium.so` for linux-x64, pinned to PDFium branch `chromium/8066` (the same revision as NuGet 156.0.8066):

- `pdf_use_skia = true` and `pdf_use_agg = true`, so `FPDF_LIBRARY_CONFIG` version 4 can pick the renderer once per process.
- `pdf_enable_v8 = false` and `pdf_enable_xfa = false`, matching the non-V8 package the C# P/Invoke was written against.
- `is_component_build = false` plus a rewrite of `component("pdfium")` to `shared_library("pdfium")`. A GN `component` is a static target when component builds are off, so that rewrite is what emits `libpdfium.so`.
- `public/fpdfview.h` keeps the default-visibility `FPDF_EXPORT` even though `COMPONENT_BUILD` is unset. Without that, `NativeLibrary.TryGetExport("FPDF_RenderPageSkia")` stays false.
- `pdf_is_complete_lib` stays unset. That flag is the fat static archive.

Pixels stay on the CPU bitmap from `FPDF_RenderPageBitmap`: BGRA, or 8-bit gray when `NativeGrayscale` is set. `FPDF_RenderPageSkia` is the probe that the build included Skia. It takes an `SkCanvas` from the Skia linked into this `libpdfium.so`. SkiaSharp's `libSkiaSharp` is a different Skia. Do not pass an `SKCanvas` into it.

A GPU-backed `SkCanvas` (`GrDirectContext`, `SkSurfaces::RenderTarget`) is not part of this recipe.

## Build

Run this on a host with tens of gigabytes of disk and enough RAM to link Skia into PDFium. A 4-core machine with about 4GB free is not enough; the link is the part that fails. `NINJA_JOBS` defaults to 2.

```bash
bash etc/pdfium-skia/build-linux-x64.sh
```

The checkout lands in `$HOME/src/pdfium-skia-8066` (`PDFIUM_SKIA_ROOT` overrides it). The script clones `depot_tools`, syncs PDFium at `chromium/8066` with the default gclient configuration (that checkout includes `third_party/skia`; `checkout_configuration=minimal` would skip it), rewrites the shared-library bits, and runs `gn` / `ninja`.

If configure or the link complains about missing system headers:

```bash
INSTALL_BUILD_DEPS=1 bash etc/pdfium-skia/build-linux-x64.sh
```

That runs `build/install-build-deps.sh` and needs sudo.

The script refuses to finish unless `nm -D` shows `FPDF_RenderPageSkia`, `FPDF_InitLibraryWithConfig`, and `FPDF_RenderPageBitmap`. The stripped drop-in is `$PDFIUM_SKIA_ROOT/libpdfium.so`. Do not commit it. It is much larger than the stock ~7.5MB library because Skia is linked in.

`bash etc/pdfium-skia/build-linux-x64.sh --self-test` only checks the source rewrite, and does not download PDFium.

## Drop in

Put that `libpdfium.so` next to the Native AOT binary, in place of the stock one. The file name stays `libpdfium.so`, which is what `NativeLibrary.TryLoad("pdfium")` loads. Then set `PDFTOIMAGE_RENDERER=skia` before the first PDF call. Parallel workers are separate processes of the same binary; they load the same directory and inherit the variable.

`/health` on miniocr reports `pdfRenderer`, `pdfRendererRequested`, and `pdfSkiaBuild`. Compare two processes, one `agg` and one `skia`. Antialiasing and hinting can differ. Do not require byte-identical pages. PDFium's Skia path has shipped with blank-page bugs before; keep AGG as the default until a page set has been compared.

A non-AOT single-file publish extracts natives under `$HOME/.net/MiniOcr/<hash>/`. The contest layout is the AOT directory, where `libpdfium.so` sits beside `MiniOcr`.

## Why this is not Chrome, WebView2, or a COM server

Nothing already on Windows or Linux feeds this stack's contract: Native AOT, the existing process pool, and a PDFium-DPI Gray8 buffer from `FPDF_RenderPageBitmap`.

- `chrome_pdf::RenderPDFPageToBitmap` (`pdf/pdf.h`, `pdf/pdfium/pdfium_engine_exports.h`) is an internal C++ function. It fills a caller BGRA buffer through `FPDF_RenderPageBitmap`. It is not COM. Chromium folded the PDF plugin into the browser binary in 2015 and stopped exporting a stable `pdf.dll` for `LoadLibrary` / `GetProcAddress("RenderPDFPageToBitmap")`. `RenderPDFPageToDC` is Windows-only and still internal.
- There is no public Chrome COM server for page bitmaps. `Windows.Data.Pdf` is WinRT over Windows' own PDFium, not Chrome, and it does not exist on Linux. It is a poor fit for Native AOT.
- Edge WebView2 is Windows-only. `CapturePreview` is a PNG of the viewer, not a chosen-DPI Gray8 page.
- Headless Chrome (`Page.captureScreenshot`, `Page.printToPDF`) does not rasterize a page at a PDFium DPI into Gray8. Screenshots are viewport PNG or JPEG and include the viewer. `printToPDF` writes another PDF. A Chromium install is hundreds of megabytes plus libraries. A warm process is hundreds of milliseconds to start and tens of megabytes per page. A pool of those processes is not `PDFtoImage.Parallel`, and a crash needs its own supervisor.

Those options also cannot see `PDFTOIMAGE_RENDERER`. The switch in this repo only works when the loaded `libpdfium.so` was built with `PDF_USE_SKIA`.
