using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace PDFtoImage.Parallel.Internals
{
    internal static partial class FileFingerprint
    {
        internal readonly record struct Token(long FileIndex, long Volume, long ChangeTimeTicks, ulong ContentStamp);

        internal static Token Capture(FileStream stream)
        {
            ulong content;
            try
            {
                content = Content(stream);
            }
            catch (IOException)
            {
                // A stamp we could not read must not match a later call.
                return new Token(0, 0, 0, (ulong)Random.Shared.NextInt64());
            }

            try
            {
                if (OperatingSystem.IsLinux())
                {
                    var (index, change) = Linux(stream);
                    return new Token(index, 0, change, content);
                }

                if (OperatingSystem.IsWindows())
                {
                    var (index, volume) = Windows(stream);
                    return new Token(index, volume, 0, content);
                }
            }
            catch (IOException)
            {
                return new Token(0, 0, 0, content);
            }

            return new Token(0, 0, 0, content);
        }

        private static ulong Content(FileStream stream)
        {
            var length = stream.Length;
            var window = (int)Math.Min(65536, length);
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(window, 1));

            try
            {
                stream.Position = 0;
                stream.ReadExactly(buffer.AsSpan(0, window));
                var head = Mix(buffer.AsSpan(0, window));
                var tail = head;
                var mid = 0;

                if (length > window)
                {
                    var tailStart = Math.Max((long)window, length - window);
                    stream.Position = tailStart;
                    var tailCount = (int)(length - tailStart);
                    stream.ReadExactly(buffer.AsSpan(0, tailCount));
                    tail = Mix(buffer.AsSpan(0, tailCount));
                }

                if (length > window * 2L)
                {
                    stream.Position = length / 2;
                    var midCount = (int)Math.Min(window, length - stream.Position);
                    stream.ReadExactly(buffer.AsSpan(0, midCount));
                    mid = Mix(buffer.AsSpan(0, midCount));
                }

                stream.Position = 0;
                return ((ulong)(uint)head << 32) ^ (uint)tail ^ ((ulong)(uint)mid << 1);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static int Mix(ReadOnlySpan<byte> bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        private static unsafe (long Index, long ChangeTimeTicks) Linux(FileStream stream)
        {
            const int atEmptyPath = 0x1000;
            const uint statxIno = 0x100;
            const uint statxCtime = 0x80;
            var handle = stream.SafeFileHandle;
            var added = false;

            try
            {
                handle.DangerousAddRef(ref added);
                var buffer = stackalloc byte[256];
                var empty = stackalloc byte[1];
                if (statx((int)handle.DangerousGetHandle(), empty, atEmptyPath, statxIno | statxCtime, buffer) != 0)
                    return (0, 0);

                var index = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buffer + 32, 8));
                var change = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(buffer + 96, 8));
                return (index, change);
            }
            finally
            {
                if (added)
                    handle.DangerousRelease();
            }
        }

        private static (long Index, long Volume) Windows(FileStream stream)
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                return (0, 0);

            var index = ((long)info.FileIndexHigh << 32) | info.FileIndexLow;
            return (index, info.VolumeSerialNumber);
        }

        [LibraryImport("libc", SetLastError = true)]
        private static unsafe partial int statx(int dirfd, byte* pathname, int flags, uint mask, byte* statxbuf);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }
}
