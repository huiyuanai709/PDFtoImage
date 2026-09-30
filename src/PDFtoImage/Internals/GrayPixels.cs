using System;

namespace PDFtoImage.Internals
{
    /// <summary>
    /// Packs an opaque BGRA grayscale image into 8-bit rows.
    /// PDFium's native 8bpp target is a different picture from <c>FPDF_GRAYSCALE</c> into BGRA,
    /// so the gray byte is copied from the blue channel after that render.
    /// </summary>
    internal static class GrayPixels
    {
        internal static int Stride(int width) => (width + 3) & ~3;

        internal static unsafe bool TryPack(byte* source, int sourceStride, byte* destination, int destinationStride, int width, int height)
        {
            if (sourceStride < checked(width * 4) || destinationStride < width)
                return false;

            for (var y = 0; y < height; y++)
            {
                var src = source + (y * sourceStride);
                var dst = destination + (y * destinationStride);

                for (var x = 0; x < width; x++)
                {
                    var pixel = src + (x * 4);

                    // Expanding with alpha 255 only rebuilds the BGRA image when the render is opaque gray.
                    if (pixel[0] != pixel[1] || pixel[1] != pixel[2] || pixel[3] != 255)
                        return false;

                    dst[x] = pixel[0];
                }

                for (var x = width; x < destinationStride; x++)
                    dst[x] = 0;
            }

            return true;
        }
    }
}
