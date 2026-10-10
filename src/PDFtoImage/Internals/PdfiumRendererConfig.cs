using System;
using System.Runtime.InteropServices;

namespace PDFtoImage.Internals
{
    /// <summary>
    /// <c>FPDF_LIBRARY_CONFIG</c> version 4, the first version that carries <c>m_RendererType</c>.
    /// Later fields (font backend, brotli, isolate-per-document) are not sent.
    /// An AGG-only build crashes if version is at least 4 and the renderer is not AGG, so callers
    /// must not allocate this for Skia unless <c>FPDF_RenderPageSkia</c> is exported.
    /// </summary>
    internal static class PdfiumRendererConfig
    {
        internal static int NativeSize => IntPtr.Size == 8 ? 48 : 24;

        internal static IntPtr Allocate(int rendererType)
        {
            var size = NativeSize;
            var ptr = Marshal.AllocHGlobal(size);

            try
            {
                // Zero the whole struct. Unused pointers stay null, which is the default
                // font path and the "PDFium creates its own isolate" setting.
                var zeros = new byte[size];
                Marshal.Copy(zeros, 0, ptr, size);

                if (IntPtr.Size == 8)
                {
                    var config = new Config64
                    {
                        Version = 4,
                        RendererType = rendererType,
                    };
                    Marshal.StructureToPtr(config, ptr, false);
                }
                else
                {
                    var config = new Config32
                    {
                        Version = 4,
                        RendererType = rendererType,
                    };
                    Marshal.StructureToPtr(config, ptr, false);
                }

                return ptr;
            }
            catch
            {
                Marshal.FreeHGlobal(ptr);
                throw;
            }
        }

        // LP64: int, pad, two pointers, uint, pad, pointer, int, pad.
        [StructLayout(LayoutKind.Explicit, Size = 48)]
        private struct Config64
        {
            [FieldOffset(0)] public int Version;
            [FieldOffset(8)] public IntPtr UserFontPaths;
            [FieldOffset(16)] public IntPtr Isolate;
            [FieldOffset(24)] public uint V8EmbedderSlot;
            [FieldOffset(32)] public IntPtr Platform;
            [FieldOffset(40)] public int RendererType;
        }

        // ILP32: no pointer padding.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct Config32
        {
            [FieldOffset(0)] public int Version;
            [FieldOffset(4)] public IntPtr UserFontPaths;
            [FieldOffset(8)] public IntPtr Isolate;
            [FieldOffset(12)] public uint V8EmbedderSlot;
            [FieldOffset(16)] public IntPtr Platform;
            [FieldOffset(20)] public int RendererType;
        }
    }
}
