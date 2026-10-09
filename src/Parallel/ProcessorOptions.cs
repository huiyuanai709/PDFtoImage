namespace PDFtoImage.Parallel
{
    /// <summary>
    /// Settings for a <see cref="ParallelPdfProcessor"/>. The <see cref="IParallelPdfProcessor"/> reads them once during construction.
    /// </summary>
    public sealed record ProcessorOptions
    {
        /// <summary>
        /// Maximum number of worker processes kept by the pool. Workers start on demand.
        /// A positive value is required; <see langword="null"/> uses <see cref="System.Environment.ProcessorCount"/>.
        /// </summary>
        public int? WorkerCount { get; init; }

        /// <summary>
        /// Maximum number of simultaneous operations admitted to the worker pool, including document loading
        /// and page rendering. A positive value adds memory backpressure when <see cref="WorkerCount"/>
        /// is large. <see langword="null"/> adds no limit beyond <see cref="WorkerCount"/>.
        /// Input streams in <see cref="ProcessorTransferMode.Ipc"/> mode are buffered before entering these slots.
        /// Cleanup does not wait for render slots. Returned bitmaps are owned by the caller and are not counted.
        /// </summary>
        public int? SlotCount { get; init; }

        /// <summary>
        /// How PDFs and raw bitmap pixels are exchanged with workers. The default
        /// <see cref="ProcessorTransferMode.Ipc"/> buffers both through local IPC pipes;
        /// <see cref="ProcessorTransferMode.MemoryMappedFile"/> uses temporary PDF files and file-backed bitmap mappings.
        /// </summary>
        public ProcessorTransferMode TransferMode { get; init; }

        /// <summary>
        /// In <see cref="ProcessorTransferMode.MemoryMappedFile"/> mode, reuse a readable, seekable
        /// <see cref="System.IO.FileStream"/> without copying its PDF to a temporary file.
        /// Its current position is ignored; the whole file is rendered from offset zero.
        /// The file must remain unchanged while it is being rendered. If it cannot be reopened for reading,
        /// the processor copies the stream instead. The default is <see langword="true"/>; this setting has
        /// no effect in <see cref="ProcessorTransferMode.Ipc"/> mode.
        /// </summary>
        public bool ReuseFileStream { get; init; } = true;

        /// <summary>
        /// In <see cref="ProcessorTransferMode.Ipc"/> mode, open a readable <see cref="System.IO.FileStream"/>
        /// by path in the worker instead of copying the PDF through the pipe. Bitmaps still return through the pipe.
        /// The file must stay unchanged while a worker has it open. With <see cref="RetainDocuments"/> that lasts
        /// until the processor is disposed or the file length or last-write time changes.
        /// Non-file streams are still copied. The default is <see langword="false"/>, which keeps the IPC snapshot of the bytes.
        /// </summary>
        public bool ShareSourceFile { get; init; }

        /// <summary>
        /// Keep a worker's opened file PDF across calls when the path, length, creation time, last-write time,
        /// file identity, sampled contents, and password match. The next lease then skips parsing the file again.
        /// Byte-array requests and temporary copies are not reused, because those files are deleted with the call.
        /// The default is <see langword="false"/>, which unloads the document when the call finishes.
        /// A kept file stays mapped until <see cref="ParallelPdfProcessor.ReleaseRetainedFileAsync"/> or disposal.
        /// </summary>
        public bool RetainDocuments { get; init; }

        /// <summary>
        /// Start <see cref="WorkerCount"/> worker processes as soon as the processor is constructed.
        /// The first render waits for them if they are not connected yet. <see cref="ParallelPdfProcessor.PrewarmAsync"/>
        /// waits for the same startup. The default is <see langword="false"/>, which starts a worker on its first job.
        /// </summary>
        public bool PrewarmWorkers { get; init; }

        /// <summary>
        /// Directory for temporary PDF and bitmap files in <see cref="ProcessorTransferMode.MemoryMappedFile"/> mode only.
        /// <see langword="null"/> uses <see cref="System.IO.Path.GetTempPath()"/>. A specified directory is resolved
        /// to an absolute path and created when the processor is constructed. Temporary files are deleted by the host
        /// when their requests finish or are cancelled; the directory itself is retained. On a multi-user host,
        /// choose a directory that other users cannot modify.
        /// </summary>
        public string? TempDirectory { get; init; }
    }
}