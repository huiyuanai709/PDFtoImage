using System;
using System.IO;
#if NETCOREAPP && !BROWSER
using System.IO.MemoryMappedFiles;
#endif
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace PDFtoImage.Internals
{
#pragma warning disable IDE0079
#pragma warning disable CA1510
#pragma warning restore IDE0079
    internal static class StreamManager
    {
#if NET9_0_OR_GREATER
        private static readonly System.Threading.Lock _syncRoot = new();
#else
        private static readonly object _syncRoot = new();
#endif
        private static int _nextId = 1;
        private static StreamBinding?[] _slots = new StreamBinding?[4];

        public static int Register(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var binding = StreamBinding.Create(stream);

            lock (_syncRoot)
            {
                var id = _nextId++;

                if (id <= 0)
                    throw new InvalidOperationException("The PDF stream table is full.");

                var slots = _slots;

                if ((uint)id >= (uint)slots.Length)
                {
                    var grown = new StreamBinding?[Math.Max(slots.Length * 2, id + 1)];
                    Array.Copy(slots, grown, slots.Length);
                    grown[id] = binding;
                    Volatile.Write(ref _slots, grown);
                }
                else
                {
                    Volatile.Write(ref slots[id], binding);
                }

                return id;
            }
        }

        public static void Unregister(int id)
        {
            StreamBinding? binding;

            lock (_syncRoot)
            {
                var slots = _slots;

                if ((uint)id >= (uint)slots.Length)
                    return;

                binding = Interlocked.Exchange(ref slots[id], null);
            }

            binding?.Dispose();
        }

        public static Stream? Get(int id)
        {
            var slots = Volatile.Read(ref _slots);

            if ((uint)id >= (uint)slots.Length)
                return null;

            return Volatile.Read(ref slots[id])?.Stream;
        }

        // Called from the PDFium read callback, including while the global PDFium lock is held.
        // Keep this path to a memcpy when the PDF bytes are already in memory or mapped.
        internal static int CopyBlock(int id, long position, IntPtr buffer, int size)
        {
            var slots = Volatile.Read(ref _slots);

            if ((uint)id >= (uint)slots.Length)
                return 0;

            var binding = Volatile.Read(ref slots[id]);

            if (binding == null)
                return 0;

            return binding.Copy(position, buffer, size);
        }

        internal sealed class StreamBinding : IDisposable
        {
            private readonly Stream _stream;
            private GCHandle _pin;
            private unsafe byte* _pointer;
            private long _length;
            private bool _direct;
#if NETCOREAPP && !BROWSER
            private MemoryMappedFile? _mappedFile;
            private MemoryMappedViewAccessor? _mappedView;
            private bool _pointerAcquired;
#endif
            private bool _disposed;

            private StreamBinding(Stream stream)
            {
                _stream = stream;
            }

            public Stream Stream => _stream;

            public static StreamBinding Create(Stream stream)
            {
                var binding = new StreamBinding(stream);

                try
                {
#if NETCOREAPP || NETSTANDARD2_1
                if (stream.GetType() == typeof(MemoryStream) && stream is MemoryStream memory && binding.TryPinMemory(memory))
                    return binding;
#endif

#if NETCOREAPP && !BROWSER
                    if (stream.GetType() == typeof(FileStream) && stream is FileStream file && binding.TryMapFile(file))
                        return binding;
#endif
                    return binding;
                }
                catch
                {
                    binding.Dispose();
                    throw;
                }
            }

            public unsafe int Copy(long position, IntPtr destination, int count)
            {
                if (count == 0)
                    return 1;

                if (count < 0 || position < 0 || destination == IntPtr.Zero)
                    return 0;

                if (_direct)
                {
                    if ((ulong)position > (ulong)_length || (ulong)count > (ulong)_length - (ulong)position)
                        return 0;

#if NETCOREAPP || NETSTANDARD2_1
                    Buffer.MemoryCopy(_pointer + position, (void*)destination, count, count);
                    return 1;
#else
                    return 0;
#endif
                }

                return CopyFromStream(position, destination, count);
            }

            private unsafe bool TryPinMemory(MemoryStream stream)
            {
                byte[]? buffer = null;
                var origin = 0;
                long logicalLength = -1;

                if (stream.TryGetBuffer(out var segment) && segment.Array != null)
                {
                    buffer = segment.Array;
                    origin = segment.Offset;
                    logicalLength = segment.Count;
                }
#if NET8_0_OR_GREATER
                else
                {
                    buffer = MemoryStreamAccess.Buffer(stream);
                    origin = MemoryStreamAccess.Origin(stream);
                    var end = MemoryStreamAccess.Length(stream);

                    if (buffer != null && origin >= 0 && end >= origin && end <= buffer.Length)
                        logicalLength = end - origin;
                    else
                        buffer = null;
                }
#endif
                if (buffer == null || logicalLength != stream.Length || logicalLength < 0)
                    return false;

                if (origin < 0 || (ulong)origin > (ulong)buffer.Length || (ulong)logicalLength > (ulong)(buffer.Length - origin))
                    return false;

                _pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                _pointer = (byte*)_pin.AddrOfPinnedObject() + origin;
                _length = logicalLength;
                _direct = true;
                return true;
            }

#if NETCOREAPP && !BROWSER
            private unsafe bool TryMapFile(FileStream stream)
            {
                long fileLength;

                try
                {
                    fileLength = stream.Length;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (fileLength <= 0 || fileLength > int.MaxValue)
                    return false;

                try
                {
                    _mappedFile = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity: fileLength,
                        MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                    _mappedView = _mappedFile.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
                    byte* pointer = null;
                    _mappedView.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    _pointerAcquired = true;
                    _pointer = pointer + _mappedView.PointerOffset;
                    _length = fileLength;
                    _direct = true;
                    return true;
                }
                catch (IOException)
                {
                    ReleaseMapping();
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    ReleaseMapping();
                    return false;
                }
                catch (ArgumentException)
                {
                    ReleaseMapping();
                    return false;
                }
            }

            private unsafe void ReleaseMapping()
            {
                if (_pointerAcquired)
                {
                    _mappedView?.SafeMemoryMappedViewHandle.ReleasePointer();
                    _pointerAcquired = false;
                }

                _mappedView?.Dispose();
                _mappedView = null;
                _mappedFile?.Dispose();
                _mappedFile = null;
                _pointer = null;
            }
#endif

            private unsafe int CopyFromStream(long position, IntPtr destination, int count)
            {
                var stream = _stream;

                if (!stream.CanRead || !stream.CanSeek)
                    return 0;

                try
                {
                    stream.Position = position;
#if NETCOREAPP || NETSTANDARD2_1
                    var span = new Span<byte>((void*)destination, count);
                    var total = 0;

                    while (total < count)
                    {
                        var read = stream.Read(span[total..]);

                        if (read <= 0)
                            return 0;

                        total += read;
                    }
#else
                    var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(count);

                    try
                    {
                        var total = 0;

                        while (total < count)
                        {
                            var read = stream.Read(rented, total, count - total);

                            if (read <= 0)
                                return 0;

                            total += read;
                        }

                        Marshal.Copy(rented, 0, destination, count);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                    }
#endif
                    return 1;
                }
                catch (IOException)
                {
                    return 0;
                }
                catch (NotSupportedException)
                {
                    return 0;
                }
                catch (ObjectDisposedException)
                {
                    return 0;
                }
                catch (ArgumentException)
                {
                    return 0;
                }
            }

            public unsafe void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                _pointer = null;

#if NETCOREAPP && !BROWSER
                ReleaseMapping();
#endif
                if (_pin.IsAllocated)
                    _pin.Free();
            }
        }

#if NET8_0_OR_GREATER
        private static class MemoryStreamAccess
        {
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_buffer")]
            public static extern ref byte[] Buffer(MemoryStream stream);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_origin")]
            public static extern ref int Origin(MemoryStream stream);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_length")]
            public static extern ref int Length(MemoryStream stream);
        }
#endif
    }
}
