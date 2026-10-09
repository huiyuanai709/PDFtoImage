using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage.Parallel.Internals
{
    internal class WorkerPool : IDisposable, IAsyncDisposable
    {
        protected sealed class Slot
        {
            internal WorkerConnection? Worker;
        }

        private readonly Lock _gate = new();

        private readonly List<Slot> _workers = [];

        protected readonly ConcurrentStack<Slot> _available;

        protected readonly SemaphoreSlim _slots;

        private readonly SemaphoreSlim? _parallelismSlots;

        private readonly ProcessorTransferMode _transferMode;

        private readonly string _tempDirectory;

        private readonly bool _retainDocuments;

        private bool _prewarm;

        private Task? _prewarmTask;

        private readonly SemaphoreSlim _documentCleanup = new(1, 1);

        private int _epoch;

        private TaskCompletionSource _epochChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly CancellationTokenSource _shutdown = new();

        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _disposed;

        private bool _cleanupFinished;

        private List<Exception>? _cleanupErrors;

        private int _activeOperations;

        internal WorkerPool(int workerCount, int? maxParallelism = null, ProcessorTransferMode transferMode = ProcessorTransferMode.Ipc, string? tempDirectory = null, bool retainDocuments = false, bool prewarm = false)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
            if (maxParallelism is int maximum)
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
            WorkerCount = workerCount;
            _available = new ConcurrentStack<Slot>();
            _slots = new SemaphoreSlim(workerCount);
            _parallelismSlots = maxParallelism is int limit ? new SemaphoreSlim(limit) : null;
            _transferMode = transferMode;
            _tempDirectory = tempDirectory ?? Path.GetTempPath();
            _retainDocuments = retainDocuments;
            _prewarm = prewarm;
            if (prewarm)
                _prewarmTask = Track(PrewarmCoreAsync());
        }

        internal Task PrewarmAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(ParallelPdfProcessor));
                _prewarm = true;
                return _prewarmTask = _prewarmTask is { IsFaulted: false, IsCanceled: false } ? _prewarmTask : Track(PrewarmCoreAsync());
            }
        }

        private Task EnsurePrewarmedAsync()
        {
            lock (_gate)
            {
                if (_prewarmTask is { IsFaulted: false, IsCanceled: false })
                    return _prewarmTask;

                _prewarmTask = Track(PrewarmCoreAsync());
                return _prewarmTask;
            }
        }

        private static Task Track(Task task)
        {
            // A startup failure is reported to the next render. Observe it here too so an
            // unused processor does not raise an unobserved-task event.
            _ = task.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }

        private async Task PrewarmCoreAsync()
        {
            var started = new List<WorkerConnection>(WorkerCount);

            try
            {
                for (var i = 0; i < WorkerCount; i++)
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    started.Add(await StartWorkerAsync(_shutdown.Token).ConfigureAwait(false));
                }

                lock (_gate)
                {
                    if (_disposed || _shutdown.IsCancellationRequested)
                    {
                        foreach (var worker in started)
                            worker.Dispose();
                        return;
                    }

                    foreach (var worker in started)
                    {
                        var slot = new Slot { Worker = worker };
                        _workers.Add(slot);
                        _available.Push(slot);
                        PulseLocked();
                    }
                }
            }
            catch
            {
                foreach (var worker in started)
                    worker.Dispose();
                throw;
            }
        }

        protected virtual Task<WorkerConnection> StartWorkerAsync(CancellationToken cancellationToken) =>
            WorkerConnection.StartAsync(cancellationToken);

        protected virtual void StopWorkers() { }

        protected virtual void DisposeResources() { }

        public int WorkerCount { get; }

        public int[] WorkerProcessIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _workers.Where(slot => slot.Worker != null).Select(slot => slot.Worker!.ProcessId)];
                }
            }
        }

        public void ThrowIfDisposed()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(ParallelPdfProcessor));
            }
        }

        public Task<int> GetPageCountAsync(PdfRequest request, CancellationToken cancellationToken) =>
            ExecuteAsync(request, static (_, pageCount, _) => Task.FromResult(pageCount), cancellationToken);

        public Task<SKBitmap> RenderPageAsync(PdfRequest request, Index page, RenderOptions options, CancellationToken cancellationToken) =>
            ExecuteAsync(request, (worker, count, token) =>
            {
                var offset = page.GetOffset(count);

                if (offset < 0 || offset >= count)
                    throw new ArgumentOutOfRangeException(nameof(page), $"The page must be between 0 and {count - 1}.");

                return worker.RenderPageAsync(offset, options, _transferMode, _tempDirectory, token);
            }, cancellationToken);

        public Task<PdfPixels> RenderPixelsAsync(PdfRequest request, Index page, RenderOptions options, CancellationToken cancellationToken) =>
            ExecuteAsync(request, (worker, count, token) =>
            {
                var offset = page.GetOffset(count);

                if (offset < 0 || offset >= count)
                    throw new ArgumentOutOfRangeException(nameof(page), $"The page must be between 0 and {count - 1}.");

                return worker.RenderPixelsAsync(offset, options, _transferMode, _tempDirectory, token);
            }, cancellationToken);

        public async Task ReleaseDocumentAsync(PdfRequest request)
        {
            // Temporary copies are deleted when the request ends. Keeping them open would
            // pin a deleted file in the worker. Byte copies have no identity to reuse.
            // RetainDocuments leaves the file mapped until ReleaseRetainedFileAsync.
            if (_retainDocuments && !request.IsTemporaryFile && request.Identity is not null)
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;

                _activeOperations++;
            }

            var cleanupAcquired = false;

            try
            {
                // Releasing documents must not queue behind unrelated render jobs.
                // Cleanup temporarily leases every idle worker. Serialize cleanups so one
                // request cannot mistake slots held by another cleanup for busy workers and
                // leave its document loaded indefinitely. Rendering remains fully parallel.
                await _documentCleanup.WaitAsync().ConfigureAwait(false);
                cleanupAcquired = true;

                var idleSlots = new List<Slot>();

                lock (_gate)
                {
                    if (_disposed)
                        return;

                    // A zero-timeout lease is the essential part of request cleanup:
                    // workers that are now busy with another request are left alone.
                    var attempts = _available.Count;
                    for (var i = 0; i < attempts; i++)
                    {
                        if (!_slots.Wait(0) || !_available.TryPop(out var slot))
                            break;

                        idleSlots.Add(slot);
                    }
                }

                await Task.WhenAll(idleSlots.Select(slot => ReleaseDocumentFromIdleSlotAsync(slot, request))).ConfigureAwait(false);
            }
            finally
            {
                if (cleanupAcquired)
                    _documentCleanup.Release();

                lock (_gate)
                {
                    _activeOperations--;
                    CompleteDisposalIfDrained();
                }
            }
        }

        public Guid?[] WorkerDocumentIds
        {
            get
            {
                lock (_gate)
                    return [.. _workers.Where(slot => slot.Worker != null).Select(slot => slot.Worker!.DocumentId)];
            }
        }

        public int[] WorkerDocumentLoadCounts
        {
            get
            {
                lock (_gate)
                    return [.. _workers.Where(slot => slot.Worker != null).Select(slot => slot.Worker!.DocumentLoadCount)];
            }
        }

        private async Task<T> ExecuteAsync<T>(PdfRequest request,
            Func<WorkerConnection, int, CancellationToken, Task<T>> execute, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(ParallelPdfProcessor));
                _activeOperations++;
            }

            Slot? slot = null;
            WorkerConnection? worker = null;
            var acquired = false;
            var parallelismAcquired = false;

            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

                if (_prewarm)
                    await EnsurePrewarmedAsync().WaitAsync(cancellation.Token).ConfigureAwait(false);

                if (_parallelismSlots != null)
                {
                    await _parallelismSlots.WaitAsync(cancellation.Token).ConfigureAwait(false);
                    parallelismAcquired = true;
                }

                await _slots.WaitAsync(cancellation.Token).ConfigureAwait(false);
                acquired = true;

                lock (_gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!_available.TryPop(out slot))
                    {
                        slot = new Slot();
                        _workers.Add(slot);
                    }
                    worker = slot.Worker;
                }

                if (worker == null)
                {
                    worker = await StartWorkerAsync(cancellation.Token).ConfigureAwait(false);

                    lock (_gate)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        slot.Worker = worker;
                    }
                }

                return await worker.ExecuteAsync(request,
                    (pageCount, token) => execute(worker, pageCount, token), cancellation.Token).ConfigureAwait(false);
            }
            catch (ParallelConversionException exception) when (exception.RemoteExceptionType != "WorkerProcessTerminated" && worker is { IsDisposed: false })
            {
                // A complete remote error frame leaves the connection synchronized.
                throw;
            }
            catch (ArgumentOutOfRangeException) when (worker is { IsDisposed: false })
            {
                // Local page validation has not altered the IPC stream.
                throw;
            }
            catch
            {
                // A cancelled/failed frame cannot be reused. Only this lease is
                // discarded; a later job lazily creates its replacement.
                if (slot != null)
                {
                    lock (_gate)
                    {
                        slot.Worker = null;
                    }
                }

                worker?.Dispose();

                throw;
            }
            finally
            {
                lock (_gate)
                {
                    // Publishing a free slot is one state transition. ReleaseDocumentAsync
                    // observes the stack and semaphore under the same gate, so it must never
                    // see a slot before its semaphore permit (or vice versa).
                    if (slot != null)
                    {
                        _available.Push(slot);
                        PulseLocked();
                    }

                    if (acquired)
                        _slots.Release();

                    if (parallelismAcquired)
                        _parallelismSlots!.Release();

                    _activeOperations--;
                    CompleteDisposalIfDrained();
                }
            }
        }

        public void Dispose()
        {
            WorkerConnection[] workers;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                workers = [.. _workers.Where(slot => slot.Worker != null).Select(slot => slot.Worker!)];

                foreach (var slot in _workers)
                {
                    slot.Worker = null;
                }
            }

            var errors = new List<Exception>();

            TryCleanup(_shutdown.Cancel, errors);
            TryCleanup(StopWorkers, errors);

            foreach (var worker in workers)
                TryCleanup(worker.Dispose, errors);

            lock (_gate)
            {
                _cleanupErrors = errors;
                _cleanupFinished = true;

                CompleteDisposalIfDrained();

                if (errors.Count > 0)
                    throw new AggregateException("Worker pool cleanup failed.", errors);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Dispose();
            }
            catch (AggregateException) { /* Report all cleanup errors after draining below. */ }

            await _drained.Task.ConfigureAwait(false);
        }

        private async Task ReleaseDocumentFromIdleSlotAsync(Slot slot, PdfRequest request)
        {
            try
            {
                var worker = slot.Worker;

                if (worker != null)
                    await worker.UnloadDocumentAsync(request.Id, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Pool disposal is already terminating the worker.
            }
            catch
            {
                var worker = slot.Worker;

                lock (_gate)
                {
                    slot.Worker = null;
                }

                worker?.Dispose();
            }
            finally
            {
                lock (_gate)
                {
                    _available.Push(slot);
                    _slots.Release();
                    PulseLocked();
                }
            }
        }

        internal async Task ReleaseRetainedFileAsync(string fullPath, CancellationToken cancellationToken)
        {
            var counted = false;

            lock (_gate)
            {
                if (_disposed)
                    return;

                _activeOperations++;
                counted = true;
            }

            var cleanupAcquired = false;

            try
            {
                await _documentCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
                cleanupAcquired = true;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var idle = new List<Slot>();
                    int epoch;
                    var busyHolder = false;

                    lock (_gate)
                    {
                        if (_disposed)
                            return;

                        epoch = _epoch;
                        var idleSet = new HashSet<Slot>(_available);

                        foreach (var slot in _workers)
                        {
                            if (HoldsFile(slot, fullPath) && !idleSet.Contains(slot))
                                busyHolder = true;
                        }

                        var attempts = _available.Count;
                        for (var i = 0; i < attempts; i++)
                        {
                            if (!_slots.Wait(0) || !_available.TryPop(out var slot))
                                break;

                            if (HoldsFile(slot, fullPath))
                                idle.Add(slot);
                            else
                            {
                                _available.Push(slot);
                                _slots.Release();
                            }
                        }
                    }

                    if (idle.Count == 0 && !busyHolder)
                        return;

                    await Task.WhenAll(idle.Select(slot => UnloadRetainedSlotAsync(slot, fullPath))).ConfigureAwait(false);

                    if (!busyHolder)
                    {
                        lock (_gate)
                        {
                            if (_disposed || !_workers.Any(slot => HoldsFile(slot, fullPath)))
                                return;
                        }

                        continue;
                    }

                    await WaitForEpochAsync(epoch, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (cleanupAcquired)
                    _documentCleanup.Release();

                if (counted)
                {
                    lock (_gate)
                    {
                        _activeOperations--;
                        CompleteDisposalIfDrained();
                    }
                }
            }
        }

        private async Task UnloadRetainedSlotAsync(Slot slot, string fullPath)
        {
            try
            {
                var worker = slot.Worker;

                if (worker != null && HoldsFile(slot, fullPath) && worker.DocumentId is Guid id)
                    await worker.UnloadDocumentAsync(id, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Pool disposal is already terminating the worker.
            }
            catch
            {
                var worker = slot.Worker;

                lock (_gate)
                {
                    slot.Worker = null;
                }

                worker?.Dispose();
            }
            finally
            {
                lock (_gate)
                {
                    _available.Push(slot);
                    _slots.Release();
                    PulseLocked();
                }
            }
        }

        private void PulseLocked()
        {
            _epoch++;
            var pending = _epochChanged;
            _epochChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.TrySetResult();
        }

        private async Task WaitForEpochAsync(int seen, CancellationToken cancellationToken)
        {
            Task wait;

            lock (_gate)
            {
                if (_disposed || _epoch != seen)
                    return;

                wait = _epochChanged.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool HoldsFile(Slot slot, string fullPath) =>
            SameFile(slot.Worker?.LoadedFilePath, fullPath);

        private static bool SameFile(string? loaded, string fullPath)
        {
            if (string.IsNullOrEmpty(loaded))
                return false;

            string other;

            try
            {
                other = Path.GetFullPath(loaded);
            }
            catch (Exception)
            {
                other = loaded;
            }

            return OperatingSystem.IsWindows()
                ? string.Equals(other, fullPath, StringComparison.OrdinalIgnoreCase)
                : string.Equals(other, fullPath, StringComparison.Ordinal);
        }

        private void CompleteDisposalIfDrained()
        {
            if (!_cleanupFinished || _activeOperations != 0 || _drained.Task.IsCompleted)
                return;

            var errors = _cleanupErrors!;

            TryCleanup(DisposeResources, errors);
            TryCleanup(_slots.Dispose, errors);
            if (_parallelismSlots != null)
                TryCleanup(_parallelismSlots.Dispose, errors);
            TryCleanup(_documentCleanup.Dispose, errors);
            TryCleanup(_shutdown.Dispose, errors);

            if (errors.Count == 0)
                _drained.TrySetResult();
            else
                _drained.TrySetException(new AggregateException("Worker pool cleanup failed.", errors));
        }

        private static void TryCleanup(Action cleanup, List<Exception> errors)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
    }
}