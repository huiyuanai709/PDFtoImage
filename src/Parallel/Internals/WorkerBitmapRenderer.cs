using PDFtoImage.Internals;
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PDFtoImage.Parallel.Internals
{
    [SupportedOSPlatform("windows10.0")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static class WorkerBitmapRenderer
    {
        internal static void Render(Stream pipe, WorkerDocument document, int page, RenderOptions options, string? bitmapPath)
        {
            if (bitmapPath == null)
                RenderToIpc(pipe, document, page, options);
            else
                RenderToFile(pipe, document, page, options, bitmapPath);
        }

        private static unsafe void RenderToIpc(Stream pipe, WorkerDocument document, int page, RenderOptions options)
        {
            IntPtr pixels = IntPtr.Zero;
            IntPtr packed = IntPtr.Zero;
            var width = 0;
            var height = 0;
            var rowBytes = 0;

            try
            {
                document.Render(page, options, (renderWidth, renderHeight) =>
                {
                    width = renderWidth;
                    height = renderHeight;
                    rowBytes = checked(width * 4);
                    var byteCount = checked(rowBytes * height);
                    WorkerProtocol.ValidateIpcBitmapLength(byteCount);
                    pixels = Marshal.AllocHGlobal(byteCount);
                    return (pixels, rowBytes);
                });

                var gray = false;

                if (UseGray(options))
                {
                    var grayStride = GrayPixels.Stride(width);
                    var grayBytes = checked(grayStride * height);
                    WorkerProtocol.ValidateIpcBitmapLength(grayBytes);
                    packed = Marshal.AllocHGlobal(grayBytes);
                    gray = GrayPixels.TryPack((byte*)pixels, rowBytes, (byte*)packed, grayStride, width, height);

                    if (gray)
                    {
                        Marshal.FreeHGlobal(pixels);
                        pixels = IntPtr.Zero;
                        WorkerProtocol.WriteBitmapResponse(pipe, packed, width, height, grayStride, gray: true);
                        return;
                    }
                }

                WorkerProtocol.WriteBitmapResponse(pipe, pixels, width, height, rowBytes, gray: false);
            }
            finally
            {
                if (pixels != IntPtr.Zero)
                    Marshal.FreeHGlobal(pixels);

                if (packed != IntPtr.Zero)
                    Marshal.FreeHGlobal(packed);
            }
        }

        private static unsafe void RenderToFile(Stream pipe, WorkerDocument document, int page, RenderOptions options, string bitmapPath)
        {
            if (!UseGray(options))
            {
                var size = RenderBgraToFile(document, page, options, bitmapPath);
                WorkerProtocol.WriteMappedBitmapMetadataResponse(pipe, size.Width, size.Height, size.RowBytes, size.ByteCount, gray: false);
                return;
            }

            IntPtr pixels = IntPtr.Zero;
            IntPtr packed = IntPtr.Zero;
            var width = 0;
            var height = 0;
            var rowBytes = 0;

            try
            {
                document.Render(page, options, (renderWidth, renderHeight) =>
                {
                    width = renderWidth;
                    height = renderHeight;
                    rowBytes = checked(width * 4);
                    pixels = Marshal.AllocHGlobal(checked(rowBytes * height));
                    return (pixels, rowBytes);
                });

                var grayStride = GrayPixels.Stride(width);
                var grayBytes = checked(grayStride * height);
                packed = Marshal.AllocHGlobal(grayBytes);

                if (GrayPixels.TryPack((byte*)pixels, rowBytes, (byte*)packed, grayStride, width, height))
                {
                    WriteFile(bitmapPath, packed, grayBytes);
                    WorkerProtocol.WriteMappedBitmapMetadataResponse(pipe, width, height, grayStride, grayBytes, gray: true);
                }
                else
                {
                    var byteCount = checked(rowBytes * height);
                    WriteFile(bitmapPath, pixels, byteCount);
                    WorkerProtocol.WriteMappedBitmapMetadataResponse(pipe, width, height, rowBytes, byteCount, gray: false);
                }
            }
            finally
            {
                if (pixels != IntPtr.Zero)
                    Marshal.FreeHGlobal(pixels);

                if (packed != IntPtr.Zero)
                    Marshal.FreeHGlobal(packed);
            }
        }

        private static unsafe (int Width, int Height, int RowBytes, int ByteCount) RenderBgraToFile(WorkerDocument document, int page, RenderOptions options, string bitmapPath)
        {
            var width = 0;
            var height = 0;
            var rowBytes = 0;
            var byteCount = 0;

            using var file = new FileStream(bitmapPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
            MemoryMappedFile? mapping = null;
            MemoryMappedViewAccessor? view = null;
            var pointerAcquired = false;

            try
            {
                document.Render(page, options, (renderWidth, renderHeight) =>
                {
                    width = renderWidth;
                    height = renderHeight;
                    rowBytes = checked(width * 4);
                    byteCount = checked(rowBytes * height);

                    if (byteCount <= 0)
                        throw new InvalidDataException("The rendered bitmap has no pixels.");

                    file.SetLength(byteCount);
                    mapping = MemoryMappedFile.CreateFromFile(file, null, byteCount,
                        MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                    view = mapping.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Write);
                    byte* pointer = null;
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    pointerAcquired = true;
                    return ((IntPtr)(pointer + view.PointerOffset), rowBytes);
                });
            }
            finally
            {
                try
                {
                    if (pointerAcquired)
                        view!.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                finally
                {
                    try
                    {
                        view?.Dispose();
                    }
                    finally
                    {
                        mapping?.Dispose();
                    }
                }
            }

            return (width, height, rowBytes, byteCount);
        }

        private static unsafe void WriteFile(string bitmapPath, IntPtr pixels, int byteCount)
        {
            using var file = new FileStream(bitmapPath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
            file.SetLength(byteCount);
            using var mapping = MemoryMappedFile.CreateFromFile(file, null, byteCount,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            using var view = mapping.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Write);
            byte* pointer = null;

            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                Buffer.MemoryCopy(pixels.ToPointer(), pointer + view.PointerOffset, byteCount, byteCount);
            }
            finally
            {
                if (pointer != null)
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        private static bool UseGray(RenderOptions options) => options.Grayscale && !options.UseTiling;
    }
}
