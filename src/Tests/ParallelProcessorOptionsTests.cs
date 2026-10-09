#if NET11_0_OR_GREATER
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PDFtoImage.Parallel;
using PDFtoImage.Parallel.Internals;
using PDFtoImage.Internals;
using SkiaSharp;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static PDFtoImage.Tests.TestUtils;

namespace PDFtoImage.Tests
{
    [TestClass, DoNotParallelize]
    public sealed class ParallelProcessorOptionsTests : TestBase
    {
        private static readonly byte[] Pdf = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "Assets", "Wikimedia_Commons_web.pdf"));

        private static MemoryStream OpenPdf() => new(Pdf, writable: false);

        private sealed class FileFixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "PDFtoImage.Parallel.Tests." + Guid.NewGuid().ToString("N"));

            internal string InputPath => Path.Combine(_root, "input.pdf");

            internal string TempDirectory => Path.Combine(_root, "nested", "files");

            internal FileFixture(byte[]? input = null)
            {
                Directory.CreateDirectory(TempDirectory);
                File.WriteAllBytes(InputPath, input ?? Pdf);
            }

            public void Dispose()
            {
                File.Delete(InputPath);
                Directory.Delete(TempDirectory);
                Directory.Delete(Path.GetDirectoryName(TempDirectory)!);
                Directory.Delete(_root);
            }
        }

        private sealed class NonSeekableFileStream(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        {
            public override bool CanSeek => false;
        }

        private sealed class CancelledCopyStream(CancellationTokenSource cancellation) : MemoryStream(Pdf, writable: false)
        {
            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                await destination.WriteAsync(Pdf.AsMemory(0, 128), cancellationToken);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        [TestMethod]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859")]
        public async Task PublicInterfaceAndDefaultOptionsRenderWithIpc()
        {
            var defaults = new ProcessorOptions();
            Assert.IsNull(defaults.WorkerCount);
            Assert.IsNull(defaults.SlotCount);
            Assert.IsNull(defaults.TempDirectory);
            Assert.AreEqual(ProcessorTransferMode.Ipc, defaults.TransferMode);
            Assert.IsTrue(defaults.ReuseFileStream);

            var configured = defaults with { WorkerCount = 1 };
            Assert.IsNull(defaults.WorkerCount);
            Assert.AreEqual(1, configured.WorkerCount);

            await using IParallelPdfProcessor processor = new ParallelPdfProcessor(configured);
            using var actual = await processor.ToImageAsync(OpenPdf(), options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken);
            using var expected = Conversion.ToImage(Pdf, options: new RenderOptions(Dpi: 40));
            AssertBitmapsEqual(expected, actual);
        }

        [TestMethod]
        public void InvalidProcessorOptionsAreRejected()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new ParallelPdfProcessor((ProcessorOptions)null!));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ParallelPdfProcessor(new ProcessorOptions { WorkerCount = 0 }));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ParallelPdfProcessor(new ProcessorOptions { SlotCount = 0 }));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ParallelPdfProcessor(new ProcessorOptions { SlotCount = -1 }));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ParallelPdfProcessor(new ProcessorOptions { TransferMode = (ProcessorTransferMode)99 }));
            using var ipc = new ParallelPdfProcessor(new ProcessorOptions { TempDirectory = "\0" });
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        public async Task MappedModeRendersOrderedPagesAndDeletesTemporaryFiles(int slotCount)
        {
            var root = Path.Combine(Path.GetTempPath(), "PDFtoImage.Parallel.Tests." + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "nested", "files");
            try
            {
                await using var processor = new ParallelPdfProcessor(new ProcessorOptions
                {
                    WorkerCount = 2,
                    SlotCount = slotCount == 0 ? null : slotCount,
                    TransferMode = ProcessorTransferMode.MemoryMappedFile,
                    TempDirectory = directory
                });
                var pages = new[] { 2, 0, 1 };
                var index = 0;
                await foreach (var image in processor.ToImagesAsync(OpenPdf(), pages, options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken))
                {
                    using (image)
                    using (var expected = Conversion.ToImage(Pdf, pages[index++], options: new RenderOptions(Dpi: 40)))
                        AssertBitmapsEqual(expected, image);

                    var paths = processor.TemporaryPdfPaths;
                    Assert.HasCount(1, paths);
                    Assert.AreEqual(directory, Path.GetDirectoryName(paths[0]));
                    Assert.IsTrue(paths[0].EndsWith(".pdf", StringComparison.Ordinal));
                    Assert.IsTrue(File.Exists(paths[0]), "The PDF must remain available to every worker until enumeration finishes.");
                    if (OperatingSystem.IsWindows())
                        Assert.IsTrue(File.GetAttributes(paths[0]).HasFlag(FileAttributes.Temporary));
                }

                Assert.AreEqual(pages.Length, index);
                Assert.IsEmpty(processor.TemporaryPdfPaths);
                Assert.IsEmpty(Directory.GetFiles(directory), "The host must delete every PDF and mapped bitmap when enumeration finishes.");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
                if (Directory.Exists(Path.Combine(root, "nested")))
                    Directory.Delete(Path.Combine(root, "nested"));
                if (Directory.Exists(root))
                    Directory.Delete(root);
            }
        }

        [TestMethod]
        public async Task MappedSinglePageDeletesBitmapBeforeReturning()
        {
            var root = Path.Combine(Path.GetTempPath(), "PDFtoImage.Parallel.Tests." + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "nested", "files");
            try
            {
                await using var processor = new ParallelPdfProcessor(new ProcessorOptions
                {
                    WorkerCount = 1,
                    TransferMode = ProcessorTransferMode.MemoryMappedFile,
                    TempDirectory = directory
                });
                using var image = await processor.ToImageAsync(OpenPdf(), options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken);
                Assert.IsEmpty(Directory.GetFiles(directory));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
                if (Directory.Exists(Path.Combine(root, "nested")))
                    Directory.Delete(Path.Combine(root, "nested"));
                if (Directory.Exists(root))
                    Directory.Delete(root);
            }
        }

        [TestMethod]
        public async Task MappedFileStreamIsReusedWithoutCreatingTemporaryPdf()
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 2,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory
            });
            using var source = File.OpenRead(fixture.InputPath);
            var pages = new[] { 1, 0 };
            var index = 0;
            await foreach (var image in processor.ToImagesAsync(source, pages, leaveOpen: true,
                options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken))
            {
                using (image)
                using (var expected = Conversion.ToImage(Pdf, pages[index++], options: new RenderOptions(Dpi: 40)))
                    AssertBitmapsEqual(expected, image);

                Assert.IsEmpty(processor.TemporaryPdfPaths);
                Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory, "*.pdf"));
            }

            Assert.AreEqual(pages.Length, index);
            Assert.AreEqual(0, source.Position);
            Assert.IsTrue(source.CanRead);
            Assert.IsTrue(File.Exists(fixture.InputPath));
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
        }

        [TestMethod]
        public async Task FileStreamCopyCanBeForcedAndIsUsedForExclusiveSharing()
        {
            using var fixture = new FileFixture();
            await using var copyProcessor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory,
                ReuseFileStream = false
            });
            using (var source = File.OpenRead(fixture.InputPath))
            {
                await using var iterator = copyProcessor.ToImagesAsync(source, [0], leaveOpen: true,
                    options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken).GetAsyncEnumerator(TestContext!.CancellationToken);
                Assert.IsTrue(await iterator.MoveNextAsync());
                iterator.Current.Dispose();
                Assert.HasCount(1, copyProcessor.TemporaryPdfPaths);
            }
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));

            await using var fallbackProcessor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory
            });
            using (var exclusive = new FileStream(fixture.InputPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await using var iterator = fallbackProcessor.ToImagesAsync(exclusive, [0], leaveOpen: true,
                    options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken).GetAsyncEnumerator(TestContext!.CancellationToken);
                Assert.IsTrue(await iterator.MoveNextAsync());
                iterator.Current.Dispose();
                Assert.HasCount(1, fallbackProcessor.TemporaryPdfPaths);
            }
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));

            using (var nonSeekable = new NonSeekableFileStream(fixture.InputPath))
            {
                await using var iterator = fallbackProcessor.ToImagesAsync(nonSeekable, [0], leaveOpen: true,
                    options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken).GetAsyncEnumerator(TestContext!.CancellationToken);
                Assert.IsTrue(await iterator.MoveNextAsync());
                iterator.Current.Dispose();
                Assert.HasCount(1, fallbackProcessor.TemporaryPdfPaths);
            }
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
        }

        [TestMethod]
        [DataRow(ProcessorTransferMode.MemoryMappedFile, true)]
        [DataRow(ProcessorTransferMode.MemoryMappedFile, false)]
        [DataRow(ProcessorTransferMode.Ipc, true)]
        public async Task FileStreamAtNonzeroPositionRendersWholePdf(ProcessorTransferMode mode, bool reuseFileStream)
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = mode,
                TempDirectory = fixture.TempDirectory,
                ReuseFileStream = reuseFileStream
            });
            using var source = File.OpenRead(fixture.InputPath);
            source.Position = 4;

            await using (var iterator = processor.ToImagesAsync(source, [0], leaveOpen: true,
                options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken).GetAsyncEnumerator(TestContext!.CancellationToken))
            {
                Assert.IsTrue(await iterator.MoveNextAsync());
                using var image = iterator.Current;
                using var expected = Conversion.ToImage(Pdf, options: new RenderOptions(Dpi: 40));
                AssertBitmapsEqual(expected, image);

                if (mode == ProcessorTransferMode.MemoryMappedFile && !reuseFileStream)
                    Assert.HasCount(1, processor.TemporaryPdfPaths);
                else
                    Assert.IsEmpty(processor.TemporaryPdfPaths);
            }

            Assert.AreEqual(reuseFileStream && mode == ProcessorTransferMode.MemoryMappedFile ? 4 : source.Length, source.Position);
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
        }

        [TestMethod]
        [DataRow(ProcessorTransferMode.Ipc)]
        [DataRow(ProcessorTransferMode.MemoryMappedFile)]
        public async Task NativeGrayscaleWorkersMatchInProcessGray(ProcessorTransferMode mode)
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = mode,
                TempDirectory = fixture.TempDirectory,
                ShareSourceFile = mode == ProcessorTransferMode.Ipc
            });
            var options = new RenderOptions(Dpi: 40, AntiAliasing: PdfAntiAliasing.None, Grayscale: true, Rotation: PdfRotation.Rotate90)
            {
                NativeGrayscale = true
            };

            using var actual = await processor.ToImageAsync(File.OpenRead(fixture.InputPath), leaveOpen: false, options: options, cancellationToken: TestContext!.CancellationToken);
            using var session = PdfSession.Open(new MemoryStream(Pdf), leaveOpen: true);
            using var native = session.RenderPixels(0, options);
            var bitmap = actual.GetPixelSpan();
            var gray = native.Pixels.Span;

            Assert.AreEqual(native.Width, actual.Width);
            Assert.AreEqual(native.Height, actual.Height);
            Assert.AreEqual(SKColorType.Gray8, native.ColorType);
            for (var y = 0; y < native.Height; y++)
            {
                var row = gray.Slice(y * native.RowBytes, native.Width);
                var bgra = bitmap.Slice(y * actual.RowBytes, native.Width * 4);
                for (var x = 0; x < native.Width; x++)
                {
                    var sample = row[x];
                    Assert.AreEqual(sample, bgra[x * 4]);
                    Assert.AreEqual(sample, bgra[x * 4 + 1]);
                    Assert.AreEqual(sample, bgra[x * 4 + 2]);
                    Assert.AreEqual(byte.MaxValue, bgra[x * 4 + 3]);
                }
            }
        }

        [TestMethod]
        public async Task SharedFileIpcSkipsTheByteCopyAndCanRetainTheDocument()
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                ShareSourceFile = true,
                RetainDocuments = true
            });
            var options = new RenderOptions(Dpi: 40);
            using var source = File.OpenRead(fixture.InputPath);
            source.Position = 4;

            using (var image = await processor.ToImageAsync(source, leaveOpen: true, options: options, cancellationToken: TestContext!.CancellationToken))
            using (var expected = Conversion.ToImage(Pdf, options: options))
                AssertBitmapsEqual(expected, image);

            Assert.AreEqual(4, source.Position);
            Assert.IsEmpty(processor.TemporaryPdfPaths);
            Assert.AreSequenceEqual([1], processor.WorkerDocumentLoadCounts);

            using (var again = await processor.ToImageAsync(File.OpenRead(fixture.InputPath), leaveOpen: false, options: options, cancellationToken: TestContext.CancellationToken))
            using (var expected = Conversion.ToImage(Pdf, options: options))
                AssertBitmapsEqual(expected, again);

            Assert.AreSequenceEqual([1], processor.WorkerDocumentLoadCounts);
            Assert.IsTrue(processor.WorkerDocumentIds.All(id => id != null));

            File.SetLastWriteTimeUtc(fixture.InputPath, DateTime.UtcNow.AddMinutes(5));
            using var reloaded = await processor.ToImageAsync(File.OpenRead(fixture.InputPath), options: options, cancellationToken: TestContext.CancellationToken);
            using var reloadedExpected = Conversion.ToImage(Pdf, options: options);
            AssertBitmapsEqual(reloadedExpected, reloaded);
            Assert.AreSequenceEqual([2], processor.WorkerDocumentLoadCounts);
        }

        [TestMethod]
        public async Task IpcModeStillCopiesFileStreamAndMappedReuseDoesNotDeleteSource()
        {
            using var fixture = new FileFixture();
            await using (var ipc = new ParallelPdfProcessor(new ProcessorOptions { WorkerCount = 1, ReuseFileStream = true }))
            using (var source = File.OpenRead(fixture.InputPath))
            {
                using var image = await ipc.ToImageAsync(source, leaveOpen: true,
                    options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken);
                Assert.AreEqual(source.Length, source.Position);
            }

            var mapped = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory
            });
            await using var iterator = mapped.ToImagesAsync(File.OpenRead(fixture.InputPath), [0],
                options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken).GetAsyncEnumerator(TestContext!.CancellationToken);
            Assert.IsTrue(await iterator.MoveNextAsync());
            iterator.Current.Dispose();
            Assert.IsEmpty(mapped.TemporaryPdfPaths);
            if (OperatingSystem.IsWindows())
                Assert.ThrowsExactly<IOException>(() => new FileStream(fixture.InputPath, FileMode.Open, FileAccess.Read, FileShare.None));
            await mapped.DisposeAsync();
            using (var exclusive = new FileStream(fixture.InputPath, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            Assert.IsTrue(File.Exists(fixture.InputPath));
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
        }

        [TestMethod]
        public async Task UnreadableFileStreamFallsBackAndLeavesNoTemporaryFile()
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory
            });
            using var source = new FileStream(fixture.InputPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            await Assert.ThrowsAsync<NotSupportedException>(() => processor.ToImageAsync(source,
                leaveOpen: true, cancellationToken: TestContext!.CancellationToken));
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
            Assert.IsTrue(File.Exists(fixture.InputPath));
        }

        [TestMethod]
        public async Task FailedDirectFileRenderKeepsSourceAndReleasesHandle()
        {
            using var fixture = new FileFixture([1, 2, 3]);
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory
            });
            await Assert.ThrowsExactlyAsync<ParallelConversionException>(() => processor.ToImageAsync(
                File.OpenRead(fixture.InputPath), cancellationToken: TestContext!.CancellationToken));
            Assert.IsTrue(File.Exists(fixture.InputPath));
            Assert.IsEmpty(processor.TemporaryPdfPaths);
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
            using var exclusive = new FileStream(fixture.InputPath, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        [TestMethod]
        public void PdfDocumentCanRenderDirectlyIntoCallerPixels()
        {
            using var document = PdfDocument.Load(OpenPdf(), null, disposeStream: true);
            foreach (var options in new[]
            {
                new RenderOptions(Dpi: 40, Rotation: PdfRotation.Rotate90, Grayscale: true),
                new RenderOptions(Width: 4100, Height: 120, UseTiling: true)
            })
            {
                using var expected = Conversion.ToImage(Pdf, options: options);
                var pixels = Marshal.AllocHGlobal(expected.ByteCount);
                try
                {
                    document.Render(0, options, pixels, expected.RowBytes, TestContext!.CancellationToken);
                    using var actual = new SKBitmap();
                    Assert.IsTrue(actual.InstallPixels(new SKImageInfo(expected.Width, expected.Height,
                        SKColorType.Bgra8888, SKAlphaType.Premul), pixels, expected.RowBytes));
                    AssertBitmapsEqual(expected, actual);
                }
                finally
                {
                    Marshal.FreeHGlobal(pixels);
                }
            }
        }

        [TestMethod]
        public async Task WorkerDirectPixelRenderMatchesSerialInBothTransferModes()
        {
            using var fixture = new FileFixture();
            foreach (var transferMode in new[] { ProcessorTransferMode.Ipc, ProcessorTransferMode.MemoryMappedFile })
            {
                await using var processor = new ParallelPdfProcessor(new ProcessorOptions
                {
                    WorkerCount = 1,
                    TransferMode = transferMode,
                    TempDirectory = fixture.TempDirectory
                });
                foreach (var options in new[]
                {
                    new RenderOptions(Dpi: 40, Rotation: PdfRotation.Rotate90, Grayscale: true),
                    new RenderOptions(Width: 4100, Height: 120, UseTiling: true)
                })
                {
                    using var expected = Conversion.ToImage(Pdf, options: options);
                    using var actual = await processor.ToImageAsync(OpenPdf(), options: options,
                        cancellationToken: TestContext!.CancellationToken);
                    AssertBitmapsEqual(expected, actual);
                    Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));
                }
            }
        }

        [TestMethod]
        public async Task MappedModeDeletesPdfAfterEarlyExitAndWorkerError()
        {
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions { WorkerCount = 2, TransferMode = ProcessorTransferMode.MemoryMappedFile });
            await foreach (var image in processor.ToImagesAsync(OpenPdf(), options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken))
            {
                image.Dispose();
                break;
            }
            Assert.IsEmpty(processor.TemporaryPdfPaths);

            await Assert.ThrowsExactlyAsync<ParallelConversionException>(() => processor.ToImageAsync(new MemoryStream([1, 2, 3]), cancellationToken: TestContext.CancellationToken));
            Assert.IsEmpty(processor.TemporaryPdfPaths);
        }

        [TestMethod]
        public async Task MappedModeDeletesPartialPdfAfterCopyCancellation()
        {
            var root = Path.Combine(Path.GetTempPath(), "PDFtoImage.Parallel.Tests." + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "nested", "files");
            try
            {
                await using var processor = new ParallelPdfProcessor(new ProcessorOptions
                {
                    WorkerCount = 1,
                    TransferMode = ProcessorTransferMode.MemoryMappedFile,
                    TempDirectory = directory
                });
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext!.CancellationToken);
                await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ToImageAsync(
                    new CancelledCopyStream(cancellation), cancellationToken: cancellation.Token));
                Assert.IsEmpty(processor.TemporaryPdfPaths);
                Assert.IsEmpty(Directory.GetFiles(directory));
                Assert.IsEmpty(processor.WorkerProcessIds);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
                if (Directory.Exists(Path.Combine(root, "nested")))
                    Directory.Delete(Path.Combine(root, "nested"));
                if (Directory.Exists(root))
                    Directory.Delete(root);
            }
        }

        [TestMethod]
        public async Task ProcessorDisposalDeletesPdfHeldByPausedEnumeration()
        {
            var processor = new ParallelPdfProcessor(new ProcessorOptions { WorkerCount = 1, TransferMode = ProcessorTransferMode.MemoryMappedFile });
            await using var iterator = processor.ToImagesAsync(OpenPdf(), options: new RenderOptions(Dpi: 40), cancellationToken: TestContext!.CancellationToken).GetAsyncEnumerator(TestContext!.CancellationToken);
            Assert.IsTrue(await iterator.MoveNextAsync());
            iterator.Current.Dispose();
            var path = processor.TemporaryPdfPaths.Single();
            Assert.IsTrue(File.Exists(path));
            await processor.DisposeAsync();
            Assert.IsFalse(File.Exists(path));
        }

        [TestMethod]
        [DataRow(ProcessorTransferMode.Ipc)]
        [DataRow(ProcessorTransferMode.MemoryMappedFile)]
        public async Task ToPixelsReturnsGrayWithoutExpanding(ProcessorTransferMode mode)
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = mode,
                TempDirectory = fixture.TempDirectory,
                ShareSourceFile = mode == ProcessorTransferMode.Ipc,
                RetainDocuments = true
            });
            var options = new RenderOptions(Dpi: 40, AntiAliasing: PdfAntiAliasing.None, Grayscale: true)
            {
                NativeGrayscale = true
            };
            using var session = PdfSession.Open(new MemoryStream(Pdf), leaveOpen: true);
            using var expected = session.RenderPixels(0, options);
            using var actual = await processor.ToPixelsAsync(File.OpenRead(fixture.InputPath), leaveOpen: false, options: options, cancellationToken: TestContext!.CancellationToken);

            Assert.AreEqual(SKColorType.Gray8, actual.ColorType);
            Assert.AreEqual(expected.Width, actual.Width);
            Assert.AreEqual(expected.Height, actual.Height);
            Assert.AreEqual(expected.RowBytes, actual.RowBytes);
            Assert.IsTrue(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span));
            Assert.IsLessThan(expected.Width * expected.Height * 4, actual.ByteCount);

            await foreach (var page in processor.ToImagesPixelsAsync(File.OpenRead(fixture.InputPath), new[] { 0 }, leaveOpen: false, options: options, cancellationToken: TestContext.CancellationToken))
            {
                using (page)
                    Assert.IsTrue(expected.Pixels.Span.SequenceEqual(page.Pixels.Span));
            }

            Assert.AreSequenceEqual([1], processor.WorkerDocumentLoadCounts);
        }

        [TestMethod]
        public async Task PrewarmStartsWorkersBeforeTheFirstRender()
        {
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions { WorkerCount = 2, PrewarmWorkers = true });
            await processor.PrewarmAsync(TestContext!.CancellationToken);
            var ids = processor.WorkerProcessIds;
            Assert.HasCount(2, ids);
            using var image = await processor.ToImageAsync(OpenPdf(), options: new RenderOptions(Dpi: 40), cancellationToken: TestContext.CancellationToken);
            Assert.AreSequenceEqual(ids, processor.WorkerProcessIds);
            Assert.IsGreaterThan(0, image.Width);
        }

        [TestMethod]
        public async Task RetainSkipsTemporaryCopiesAndReloadsReplacedBytes()
        {
            using var fixture = new FileFixture();
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory,
                RetainDocuments = true
            });
            var options = new RenderOptions(Dpi: 40);
            using (var first = await processor.ToImageAsync(new MemoryStream(Pdf), leaveOpen: true, options: options, cancellationToken: TestContext!.CancellationToken))
            using (var expected = Conversion.ToImage(Pdf, options: options))
                AssertBitmapsEqual(expected, first);

            Assert.IsTrue(processor.WorkerDocumentIds.All(id => id == null));
            Assert.IsEmpty(Directory.GetFiles(fixture.TempDirectory));

            using (var second = await processor.ToImageAsync(new MemoryStream(Pdf), leaveOpen: true, options: options, cancellationToken: TestContext.CancellationToken))
            using (var expected = Conversion.ToImage(Pdf, options: options))
                AssertBitmapsEqual(expected, second);

            Assert.AreSequenceEqual([2], processor.WorkerDocumentLoadCounts);

            var copy = Path.Combine(fixture.TempDirectory, "copy.pdf");
            File.WriteAllBytes(copy, Pdf);
            await using var files = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 1,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory,
                RetainDocuments = true
            });
            using (var image = await files.ToImageAsync(File.OpenRead(copy), leaveOpen: false, options: options, cancellationToken: TestContext.CancellationToken))
                Assert.IsGreaterThan(0, image.Width);
            Assert.AreSequenceEqual([1], files.WorkerDocumentLoadCounts);

            var created = File.GetCreationTimeUtc(copy);
            var written = File.GetLastWriteTimeUtc(copy);
            var bytes = File.ReadAllBytes(copy);
            bytes[Math.Min(4096, bytes.Length - 1)] ^= 0x22;
            File.WriteAllBytes(copy, bytes);
            try
            {
                File.SetCreationTimeUtc(copy, created);
            }
            catch (PlatformNotSupportedException) { }
            File.SetLastWriteTimeUtc(copy, written);

            using (var original = new PdfRequest(Path.Combine(fixture.TempDirectory, "stamp-a.pdf"), File.OpenRead(WritePdf(fixture.TempDirectory, "stamp-a.pdf", Pdf)), null, null, deleteFile: false))
            using (var replaced = new PdfRequest(copy, File.OpenRead(copy), null, null, deleteFile: false))
            {
                Assert.AreEqual(original.Identity!.Value.Length, replaced.Identity!.Value.Length);
                Assert.AreNotEqual(original.Identity.Value.ContentStamp, replaced.Identity.Value.ContentStamp);
            }

            using (var image = await files.ToImageAsync(File.OpenRead(copy), leaveOpen: false, options: options, cancellationToken: TestContext.CancellationToken))
                Assert.IsGreaterThan(0, image.Width);
            Assert.AreSequenceEqual([2], files.WorkerDocumentLoadCounts);
            File.Delete(copy);
            File.Delete(Path.Combine(fixture.TempDirectory, "stamp-a.pdf"));
        }

        [TestMethod]
        public async Task ReleaseRetainedFileDropsWorkerHandle()
        {
            using var fixture = new FileFixture();
            var full = Path.GetFullPath(fixture.InputPath);
            await using var processor = new ParallelPdfProcessor(new ProcessorOptions
            {
                WorkerCount = 2,
                TransferMode = ProcessorTransferMode.MemoryMappedFile,
                TempDirectory = fixture.TempDirectory,
                ReuseFileStream = true,
                RetainDocuments = true,
            });
            var options = new RenderOptions(Dpi: 40);
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            await foreach (var page in processor.ToImagesAsync(stream, [0, 1], leaveOpen: true, options: options, cancellationToken: TestContext!.CancellationToken))
                page.Dispose();

            Assert.IsTrue(processor.WorkerDocumentIds.Any(id => id != null));
            var pids = processor.WorkerProcessIds;
            if (OperatingSystem.IsLinux())
                Assert.IsTrue(pids.Any(pid => ProcessReferencesPath(pid, full)), "A render worker should still have the PDF open.");

            var other = Path.Combine(fixture.TempDirectory, "other.pdf");
            File.WriteAllBytes(other, Pdf);
            await processor.ReleaseRetainedFileAsync(other, TestContext.CancellationToken);
            Assert.IsTrue(processor.WorkerDocumentIds.Any(id => id != null), "Releasing a different path must leave the retained PDF loaded.");
            if (OperatingSystem.IsLinux())
                Assert.IsTrue(pids.Any(pid => ProcessReferencesPath(pid, full)));

            await processor.ReleaseRetainedFileAsync(full, TestContext.CancellationToken);
            Assert.IsTrue(processor.WorkerDocumentIds.All(id => id == null));
            if (OperatingSystem.IsLinux())
                Assert.IsFalse(pids.Any(pid => ProcessReferencesPath(pid, full)), "Workers should have closed the PDF handle and mapping.");

            var loads = processor.WorkerDocumentLoadCounts.Sum();
            using (var again = await processor.ToImageAsync(stream, leaveOpen: true, options: options, cancellationToken: TestContext.CancellationToken))
                Assert.IsGreaterThan(0, again.Width);
            Assert.IsGreaterThan(loads, processor.WorkerDocumentLoadCounts.Sum());

            await processor.ReleaseRetainedFileAsync(full, TestContext.CancellationToken);
            File.Delete(other);
        }

        private static bool ProcessReferencesPath(int pid, string fullPath)
        {
            if (pid <= 0 || !OperatingSystem.IsLinux())
                return false;

            var fdDir = "/proc/" + pid + "/fd";
            if (Directory.Exists(fdDir))
            {
                foreach (var fd in Directory.EnumerateFiles(fdDir))
                {
                    try
                    {
                        var target = File.ResolveLinkTarget(fd, returnFinalTarget: false);
                        if (target != null && string.Equals(target.FullName, fullPath, StringComparison.Ordinal))
                            return true;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            var maps = "/proc/" + pid + "/maps";
            if (!File.Exists(maps))
                return false;

            try
            {
                foreach (var line in File.ReadLines(maps))
                {
                    if (line.Contains(fullPath, StringComparison.Ordinal))
                        return true;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return false;
        }

        private static string WritePdf(string directory, string name, byte[] bytes)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        [TestMethod]
        public void RawMappedBitmapRoundTripPreservesPixels()
        {
            var path = Path.Combine(Path.GetTempPath(), "PDFtoImage.Parallel." + Guid.NewGuid().ToString("N") + ".bitmap.raw");
            try
            {
                using var original = new SKBitmap(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul);
                original.Erase(new SKColor(40, 80, 120, 160));
                using var pipe = new MemoryStream();
                using (var creator = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                WorkerProtocol.WriteMappedBitmapResponse(pipe, original, path);
                pipe.Position = 0;
                var response = WorkerProtocol.ReadMessage(pipe)!;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                using var decoded = WorkerProtocol.ReadMappedBitmap(response, 1, file);
                AssertBitmapsEqual(original, decoded);
                Assert.AreEqual(original.ByteCount, new FileInfo(path).Length);
                Assert.ThrowsExactly<InvalidDataException>(() => WorkerProtocol.ReadMappedBitmap([1, 2, 3], 0, file));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
#endif