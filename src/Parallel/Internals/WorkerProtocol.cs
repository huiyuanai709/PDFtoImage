using SkiaSharp;
using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage.Parallel.Internals
{
    internal enum WorkerCommand : byte
    {
        LoadDocument = 1,

        RenderPage = 2,

        UnloadDocument = 3,

        LoadDocumentFile = 4
    }

    internal enum WorkerResponse : byte
    {
        Success = 1,

        Error = 2,

        Hello = 3
    }

    internal static class WorkerProtocol
    {
        internal const int Version = 2;

        private const int MaximumMessageLength = 1024 * 1024 * 1024;

        internal static byte[] CreateLoadDocumentHeader(string? password, int pdfLength, Guid requestId) =>
            CreateMessage(writer =>
            {
                writer.Write((byte)WorkerCommand.LoadDocument);
                WriteNullableString(writer, password);
                writer.Write(pdfLength);
                writer.Write(requestId.ToByteArray());
            });

        internal static int GetMaximumPdfLength(string? password)
        {
            var headerLength = CreateLoadDocumentHeader(password, 0, Guid.Empty).Length;

            if (headerLength >= MaximumMessageLength)
                throw new InvalidDataException("The PDF password and protocol metadata exceed the IPC message limit.");

            return MaximumMessageLength - headerLength;
        }

        internal static byte[] CreateMessage(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                write(writer);
            }

            return stream.ToArray();
        }

        internal static void WriteMessage(Stream stream, byte[] message) =>
            WriteMessage(stream, message, []);

        internal static void WriteMessage(Stream stream, byte[] message, ReadOnlySpan<byte> suffix)
        {
            if ((long)message.Length + suffix.Length > MaximumMessageLength)
                throw new InvalidDataException("The IPC message is too large.");

            var header = BitConverter.GetBytes(message.Length + suffix.Length);

            stream.Write(header);
            stream.Write(message);

            if (!suffix.IsEmpty)
                stream.Write(suffix);

            stream.Flush();
        }

        internal static Task WriteMessageAsync(Stream stream, byte[] message, CancellationToken cancellationToken) =>
            WriteMessageAsync(stream, message, ReadOnlyMemory<byte>.Empty, cancellationToken);

        internal static async Task WriteMessageAsync(Stream stream, byte[] message, ReadOnlyMemory<byte> suffix, CancellationToken cancellationToken)
        {
            if ((long)message.Length + suffix.Length > MaximumMessageLength)
                throw new InvalidDataException("The IPC message is too large.");

            var header = BitConverter.GetBytes(message.Length + suffix.Length);

            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);

            if (!suffix.IsEmpty)
                await stream.WriteAsync(suffix, cancellationToken).ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        internal static byte[]? ReadMessage(Stream stream)
        {
            var header = new byte[sizeof(int)];
            var firstRead = stream.Read(header, 0, header.Length);

            if (firstRead == 0)
                return null;

            ReadExactly(stream, header, firstRead, header.Length - firstRead);

            var messageLength = BitConverter.ToInt32(header, 0);

            if (messageLength <= 0 || messageLength > MaximumMessageLength)
                throw new InvalidDataException("The IPC message has an invalid length.");

            var message = new byte[messageLength];

            ReadExactly(stream, message, 0, message.Length);

            return message;
        }

        internal static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[sizeof(int)];
            var firstRead = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);

            if (firstRead == 0)
                return null;

            await ReadExactlyAsync(stream, header, firstRead, header.Length - firstRead, cancellationToken).ConfigureAwait(false);

            var messageLength = BitConverter.ToInt32(header, 0);

            if (messageLength <= 0 || messageLength > MaximumMessageLength)
                throw new InvalidDataException("The IPC message has an invalid length.");

            var message = new byte[messageLength];

            await ReadExactlyAsync(stream, message, 0, message.Length, cancellationToken).ConfigureAwait(false);

            return message;
        }

        internal static BinaryReader CreateReader(byte[] message)
        {
            return new BinaryReader(new MemoryStream(message, false), Encoding.UTF8, false);
        }

        internal static void WriteNullableString(BinaryWriter writer, string? value)
        {
            writer.Write(value != null);

            if (value != null)
                writer.Write(value);
        }

        internal static string? ReadNullableString(BinaryReader reader)
        {
            return reader.ReadBoolean() ? reader.ReadString() : null;
        }

        internal static void WriteRenderOptions(BinaryWriter writer, RenderOptions options) =>
            JsonSerializer.Serialize(writer.BaseStream, options, WorkerJsonSerializerContext.Default.RenderOptions);

        internal static RenderOptions ReadRenderOptions(BinaryReader reader)
        {
            try
            {
                return JsonSerializer.Deserialize(reader.BaseStream, WorkerJsonSerializerContext.Default.RenderOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The render options payload is invalid.", exception);
            }
        }

        internal static void ValidateIpcBitmapLength(int byteCount)
        {
            const int bitmapMetadataLength = 1 + 6 * sizeof(int);
            if (byteCount <= 0 || (long)bitmapMetadataLength + byteCount > MaximumMessageLength)
                throw new InvalidDataException("The rendered bitmap exceeds the IPC message limit.");
        }

        internal static void WriteBitmapResponse(Stream stream, IntPtr pixels, int width, int height, int rowBytes, bool gray)
        {
            var byteCount = checked(rowBytes * height);
            ValidateIpcBitmapLength(byteCount);
            var metadata = CreateBitmapMetadata(width, height, rowBytes, byteCount, gray);

            stream.Write(BitConverter.GetBytes(metadata.Length + byteCount));
            stream.Write(metadata);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                for (var offset = 0; offset < byteCount;)
                {
                    var count = Math.Min(buffer.Length, byteCount - offset);
                    Marshal.Copy(IntPtr.Add(pixels, offset), buffer, 0, count);
                    stream.Write(buffer, 0, count);
                    offset += count;
                }

                stream.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        internal static async Task WriteBitmapResponseAsync(Stream stream, SKBitmap bitmap, CancellationToken cancellationToken)
        {
            var metadata = CreateMessage(writer =>
            {
                writer.Write((byte)WorkerResponse.Success);
                writer.Write(bitmap.Width);
                writer.Write(bitmap.Height);
                writer.Write((int)bitmap.ColorType);
                writer.Write((int)bitmap.AlphaType);
                writer.Write(bitmap.RowBytes);
                writer.Write(bitmap.ByteCount);
            });

            if ((long)metadata.Length + bitmap.ByteCount > MaximumMessageLength)
                throw new InvalidDataException("The rendered bitmap exceeds the IPC message limit.");

            await stream.WriteAsync(BitConverter.GetBytes(metadata.Length + bitmap.ByteCount), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                for (var offset = 0; offset < bitmap.ByteCount;)
                {
                    var count = Math.Min(buffer.Length, bitmap.ByteCount - offset);
                    Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), offset), buffer, 0, count);
                    await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    offset += count;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        internal static unsafe void WriteMappedBitmapResponse(Stream stream, SKBitmap bitmap, string path)
        {
            if (bitmap.ByteCount <= 0)
                throw new InvalidDataException("The rendered bitmap has no pixels.");

            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                file.SetLength(bitmap.ByteCount);
                using var mapping = MemoryMappedFile.CreateFromFile(file, null, bitmap.ByteCount, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                using var view = mapping.CreateViewAccessor(0, bitmap.ByteCount, MemoryMappedFileAccess.Write);
                byte* pointer = null;
                try
                {
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    Buffer.MemoryCopy(bitmap.GetPixels().ToPointer(), pointer + view.PointerOffset, bitmap.ByteCount, bitmap.ByteCount);
                }
                finally
                {
                    if (pointer != null)
                        view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }

            WriteMessage(stream, CreateBitmapMetadata(bitmap));
        }

        internal static void WriteMappedBitmapMetadataResponse(Stream stream, int width, int height, int rowBytes, int byteCount, bool gray) =>
            WriteMessage(stream, CreateBitmapMetadata(width, height, rowBytes, byteCount, gray));

        internal static unsafe SKBitmap ReadMappedBitmap(byte[] payload, int offset, FileStream file)
        {
            const int metadataSize = 6 * sizeof(int);
            if (offset < 0 || payload.Length - offset != metadataSize)
                throw new InvalidDataException("A worker returned incomplete mapped bitmap metadata.");

            using var reader = CreateReader(payload);
            reader.BaseStream.Position = offset;
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var colorType = (SKColorType)reader.ReadInt32();
            var alphaType = (SKAlphaType)reader.ReadInt32();
            var rowBytes = reader.ReadInt32();
            var byteCount = reader.ReadInt32();

            if (!IsSupportedBitmap(width, height, colorType, alphaType, rowBytes, byteCount) || file.Length != byteCount)
                throw new InvalidDataException("A worker returned invalid mapped bitmap metadata.");

            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            try
            {
                if (bitmap.RowBytes < checked(width * 4) || bitmap.ByteCount < checked(bitmap.RowBytes * height))
                    throw new InvalidDataException("A worker returned incompatible bitmap metadata.");

                using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                using var view = mapping.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                try
                {
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    CopyToBgra(pointer + view.PointerOffset, byteCount, width, height, rowBytes, colorType, bitmap);
                }
                finally
                {
                    if (pointer != null)
                        view.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static byte[] CreateBitmapMetadata(int width, int height, int rowBytes, int byteCount, bool gray) => CreateMessage(writer =>
        {
            writer.Write((byte)WorkerResponse.Success);
            writer.Write(width);
            writer.Write(height);
            writer.Write((int)(gray ? SKColorType.Gray8 : SKColorType.Bgra8888));
            writer.Write((int)(gray ? SKAlphaType.Opaque : SKAlphaType.Premul));
            writer.Write(rowBytes);
            writer.Write(byteCount);
        });

        private static byte[] CreateBitmapMetadata(SKBitmap bitmap) => CreateMessage(writer =>
        {
            writer.Write((byte)WorkerResponse.Success);
            writer.Write(bitmap.Width);
            writer.Write(bitmap.Height);
            writer.Write((int)bitmap.ColorType);
            writer.Write((int)bitmap.AlphaType);
            writer.Write(bitmap.RowBytes);
            writer.Write(bitmap.ByteCount);
        });

        internal static SKBitmap ReadBitmap(byte[] payload, int offset = 0)
        {
            const int metadataSize = 6 * sizeof(int);

            if (offset < 0 || offset > payload.Length - metadataSize)
                throw new InvalidDataException("A worker returned incomplete bitmap metadata.");

            using var reader = CreateReader(payload);
            reader.BaseStream.Position = offset;

            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var colorType = (SKColorType)reader.ReadInt32();
            var alphaType = (SKAlphaType)reader.ReadInt32();
            var rowBytes = reader.ReadInt32();
            var byteCount = reader.ReadInt32();

            // Validate using wide arithmetic BEFORE allocating native memory.
            if (!IsSupportedBitmap(width, height, colorType, alphaType, rowBytes, byteCount) ||
                byteCount != payload.Length - offset - metadataSize)
                throw new InvalidDataException("A worker returned invalid bitmap metadata.");

            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

            try
            {
                if (bitmap.RowBytes < checked(width * 4) || bitmap.ByteCount < checked(bitmap.RowBytes * height))
                    throw new InvalidDataException("A worker returned incompatible bitmap metadata.");

                unsafe
                {
                    fixed (byte* source = &payload[offset + metadataSize])
                        CopyToBgra(source, byteCount, width, height, rowBytes, colorType, bitmap);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        internal static PdfPixels ReadPixels(byte[] payload, int offset = 0)
        {
            if (!TryReadBitmapHeader(payload, offset, out var width, out var height, out var colorType, out var alphaType, out var rowBytes, out var byteCount, out var pixels) ||
                byteCount != payload.Length - pixels)
                throw new InvalidDataException("A worker returned invalid bitmap metadata.");

            return CopyPixels(payload, pixels, width, height, rowBytes, byteCount, colorType, alphaType);
        }

        internal static unsafe PdfPixels ReadMappedPixels(byte[] payload, int offset, FileStream file)
        {
            if (!TryReadBitmapHeader(payload, offset, out var width, out var height, out var colorType, out var alphaType, out var rowBytes, out var byteCount, out var pixels) ||
                pixels != payload.Length || file.Length != byteCount)
                throw new InvalidDataException("A worker returned invalid mapped bitmap metadata.");

            var rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                using var view = mapping.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                try
                {
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    fixed (byte* destination = rented)
                        Buffer.MemoryCopy(pointer + view.PointerOffset, destination, rented.Length, byteCount);
                }
                finally
                {
                    if (pointer != null)
                        view.SafeMemoryMappedViewHandle.ReleasePointer();
                }

                return new PdfPixels(rented, width, height, rowBytes, colorType, alphaType, pooled: true);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw;
            }
        }

        private static bool TryReadBitmapHeader(byte[] payload, int offset, out int width, out int height, out SKColorType colorType, out SKAlphaType alphaType, out int rowBytes, out int byteCount, out int pixels)
        {
            const int metadataSize = 6 * sizeof(int);
            width = height = rowBytes = byteCount = pixels = 0;
            colorType = default;
            alphaType = default;

            if (offset < 0 || offset > payload.Length - metadataSize)
                return false;

            using var reader = CreateReader(payload);
            reader.BaseStream.Position = offset;
            width = reader.ReadInt32();
            height = reader.ReadInt32();
            colorType = (SKColorType)reader.ReadInt32();
            alphaType = (SKAlphaType)reader.ReadInt32();
            rowBytes = reader.ReadInt32();
            byteCount = reader.ReadInt32();
            pixels = offset + metadataSize;
            return IsSupportedBitmap(width, height, colorType, alphaType, rowBytes, byteCount);
        }

        private static PdfPixels CopyPixels(byte[] payload, int source, int width, int height, int rowBytes, int byteCount, SKColorType colorType, SKAlphaType alphaType)
        {
            var rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                payload.AsSpan(source, byteCount).CopyTo(rented);
                return new PdfPixels(rented, width, height, rowBytes, colorType, alphaType, pooled: true);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw;
            }
        }

        private static bool IsSupportedBitmap(int width, int height, SKColorType colorType, SKAlphaType alphaType, int rowBytes, int byteCount)
        {
            if (width <= 0 || height <= 0 || rowBytes <= 0 || byteCount <= 0 || (long)rowBytes * height != byteCount)
                return false;

            if (colorType == SKColorType.Bgra8888 && alphaType == SKAlphaType.Premul)
                return (long)width * 4 == rowBytes;

            return colorType == SKColorType.Gray8 && alphaType == SKAlphaType.Opaque && rowBytes >= width;
        }

        private static unsafe void CopyToBgra(byte* source, int byteCount, int width, int height, int rowBytes, SKColorType colorType, SKBitmap bitmap)
        {
            if (colorType == SKColorType.Bgra8888)
            {
                if (bitmap.RowBytes == rowBytes && bitmap.ByteCount == byteCount)
                    Buffer.MemoryCopy(source, bitmap.GetPixels().ToPointer(), byteCount, byteCount);
                else
                {
                    var destination = (byte*)bitmap.GetPixels();

                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(source + (y * rowBytes), destination + (y * bitmap.RowBytes), bitmap.RowBytes, rowBytes);
                }

                return;
            }

            var target = (byte*)bitmap.GetPixels();

            for (var y = 0; y < height; y++)
            {
                var src = source + (y * rowBytes);
                var dst = target + (y * bitmap.RowBytes);

                for (var x = 0; x < width; x++)
                {
                    var gray = src[x];
                    dst[x * 4] = gray;
                    dst[x * 4 + 1] = gray;
                    dst[x * 4 + 2] = gray;
                    dst[x * 4 + 3] = 255;
                }
            }
        }

        internal static byte[] CreateErrorResponse(Exception exception)
        {
            return CreateMessage(writer =>
            {
                writer.Write((byte)WorkerResponse.Error);
                writer.Write(exception.GetType().FullName ?? exception.GetType().Name);
                writer.Write(exception.Message);
                WriteNullableString(writer, exception.StackTrace);
            });
        }

        internal static void ThrowIfError(BinaryReader reader)
        {
            var response = (WorkerResponse)reader.ReadByte();

            if (response == WorkerResponse.Success)
                return;

            if (response != WorkerResponse.Error)
                throw new InvalidDataException("The worker returned an invalid response.");

            throw new ParallelConversionException(reader.ReadString(), reader.ReadString(), ReadNullableString(reader));
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);

                if (read == 0)
                    throw new EndOfStreamException("The worker closed its IPC pipe unexpectedly.");

                offset += read;
                count -= read;
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    throw new EndOfStreamException("The worker closed its IPC pipe unexpectedly.");

                offset += read;
                count -= read;
            }
        }
    }
}