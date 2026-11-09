using Microsoft.VisualStudio.TestTools.UnitTesting;
using PDFtoImage.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PDFtoImage.Tests
{
    [TestClass]
    public class ByteRangeTests : TestBase
    {
        [TestMethod]
        public async Task ProviderCallbackRendersWithoutFullFetch()
        {
            var pdfBytes = ReadAsset("Wikimedia_Commons_web.pdf");
            Assert.IsGreaterThan(1_000_000, pdfBytes.Length);

            var fetches = new List<(long Offset, int Count)>();
            var provider = PdfRandomAccess.Create(pdfBytes.Length, (offset, buffer, bufferOffset, count) =>
            {
                lock (fetches)
                    fetches.Add((offset, count));
                Buffer.BlockCopy(pdfBytes, (int)offset, buffer, bufferOffset, count);
            });

            using var stream = await PdfByteRangeStream.OpenAsync(provider, new PdfByteRangeOptions
            {
                HeadProbeBytes = 64 * 1024,
                TailProbeBytes = 256 * 1024,
                MinRangeRequestBytes = 64 * 1024,
            });

            Assert.AreEqual(false, stream.IsLinearized);
            Assert.IsLessThan(pdfBytes.Length, stream.BytesFetched);

            using var session = PdfSession.Open(stream, leaveOpen: true);
            using var bitmap = session.Render(0, new RenderOptions(Dpi: 72));
            Assert.IsGreaterThan(0, bitmap.ByteCount);
            Assert.IsLessThan(pdfBytes.Length, stream.BytesFetched);
            Assert.IsTrue(fetches.Count >= 2);
            Assert.IsTrue(fetches.Any(f => f.Offset == 0));
        }

        [TestMethod]
        public async Task FromStreamProviderWorks()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var memory = new MemoryStream(pdfBytes, writable: false);
            var provider = PdfRandomAccess.FromStream(memory, leaveOpen: true);

            using var stream = await PdfByteRangeStream.OpenAsync(provider, new PdfByteRangeOptions
            {
                HeadProbeBytes = 4096,
                TailProbeBytes = 4096,
                MinRangeRequestBytes = 4096,
            });

            using var session = PdfSession.Open(stream, leaveOpen: true);
            Assert.AreEqual(1, session.PageCount);
        }

        [TestMethod]
        public async Task LinearizedHeadProbeOnlyBeforeRender()
        {
            var pdfBytes = ReadAsset("hundesteuer-anmeldung.pdf");
            var provider = PdfRandomAccess.Create(pdfBytes.Length, (offset, buffer, bufferOffset, count) =>
                Buffer.BlockCopy(pdfBytes, (int)offset, buffer, bufferOffset, count));

            using var stream = await PdfByteRangeStream.OpenAsync(provider, new PdfByteRangeOptions
            {
                HeadProbeBytes = 64 * 1024,
                TailProbeBytes = 64 * 1024,
            });

            Assert.AreEqual(true, stream.IsLinearized);
            Assert.IsLessThan(pdfBytes.Length, stream.BytesFetched);

            using var session = PdfSession.Open(stream, leaveOpen: true);
            using var bitmap = session.Render(0, new RenderOptions(Dpi: 72));
            Assert.IsGreaterThan(0, bitmap.ByteCount);
        }

        [TestMethod]
        public async Task MaxFetchBytesIsEnforced()
        {
            var pdfBytes = ReadAsset("Wikimedia_Commons_web.pdf");
            var provider = PdfRandomAccess.Create(pdfBytes.Length, (offset, buffer, bufferOffset, count) =>
                Buffer.BlockCopy(pdfBytes, (int)offset, buffer, bufferOffset, count));

            await Assert.ThrowsExactlyAsync<PdfByteRangeFetchLimitException>(async () =>
            {
                _ = await PdfByteRangeStream.OpenAsync(provider, new PdfByteRangeOptions
                {
                    HeadProbeBytes = 64 * 1024,
                    TailProbeBytes = 256 * 1024,
                    MaxFetchBytes = 8 * 1024,
                });
            });
        }

        [TestMethod]
        public async Task OpenSessionAsyncConvenienceWorks()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            var provider = PdfRandomAccess.Create(pdfBytes.Length, (offset, buffer, bufferOffset, count) =>
                Buffer.BlockCopy(pdfBytes, (int)offset, buffer, bufferOffset, count));

            using var session = await PdfByteRangeStream.OpenSessionAsync(provider);
            Assert.AreEqual(1, session.PageCount);
        }

        [TestMethod]
        public async Task HttpAdapterOptionalPathStillWorks()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);

            using var stream = await PdfHttpByteRangeProvider.OpenStreamAsync(server.Uri, rangeOptions: new PdfByteRangeOptions
            {
                HeadProbeBytes = 4096,
                TailProbeBytes = 4096,
                MinRangeRequestBytes = 4096,
            });

            Assert.IsTrue(stream.Provider is PdfHttpByteRangeProvider http && http.SupportsRange);
            using var session = PdfSession.Open(stream, leaveOpen: true);
            Assert.AreEqual(1, session.PageCount);
        }

        [TestMethod]
        public async Task HttpFallbackWhenRangeUnsupported()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: false);

            using var provider = await PdfHttpByteRangeProvider.CreateAsync(server.Uri, allowFullDownloadFallback: true);
            Assert.IsFalse(provider.SupportsRange);

            using var stream = await PdfByteRangeStream.OpenAsync(provider);
            using var session = PdfSession.Open(stream, leaveOpen: true);
            Assert.AreEqual(1, session.PageCount);
        }

        [TestMethod]
        public async Task HttpDisallowFallbackThrows()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: false);

            await Assert.ThrowsExactlyAsync<PdfHttpRangeException>(async () =>
            {
                _ = await PdfHttpByteRangeProvider.CreateAsync(server.Uri, allowFullDownloadFallback: false);
            });
        }


        [TestMethod]
        public async Task HttpToImagesAsync_UsesPageRange()
        {
            var pdfBytes = ReadAsset("hundesteuer-anmeldung.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);

            var images = await PdfHttpByteRangeProvider.ToImagesAsync(server.Uri, pages: 0..1);
            try
            {
                Assert.AreEqual(1, images.Count);
                Assert.IsGreaterThan(0, images[0].ByteCount);
            }
            finally
            {
                foreach (var image in images)
                    image.Dispose();
            }
        }

        private static byte[] ReadAsset(string fileName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "..", "Assets", fileName);
            if (!File.Exists(path))
                Assert.Inconclusive($"Fixture {fileName} is missing.");
            return File.ReadAllBytes(path);
        }

        private sealed class RangeTestServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly byte[] _pdf;
            private readonly bool _supportRange;
            private readonly CancellationTokenSource _cts = new();
            private readonly Task _loop;

            private RangeTestServer(HttpListener listener, Uri uri, byte[] pdf, bool supportRange, Task loop)
            {
                _listener = listener;
                Uri = uri;
                _pdf = pdf;
                _supportRange = supportRange;
                _loop = loop;
            }

            public Uri Uri { get; }

            public static RangeTestServer Start(byte[] pdf, bool supportRange)
            {
                Exception? last = null;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var port = GetFreePort();
                    var prefix = $"http://127.0.0.1:{port}/";
                    var listener = new HttpListener();
                    listener.Prefixes.Add(prefix);
                    try
                    {
                        listener.Start();
                        return CreateRunning(listener, new Uri(prefix + "doc.pdf"), pdf, supportRange);
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { listener.Close(); } catch { /* ignore */ }
                    }
                }

                throw new InvalidOperationException("Could not start HttpListener for Range tests.", last);
            }

            private static RangeTestServer CreateRunning(HttpListener listener, Uri uri, byte[] pdf, bool supportRange)
            {
                RangeTestServer? server = null;
                var loop = Task.Run(async () =>
                {
                    while (true)
                    {
                        var local = Volatile.Read(ref server);
                        if (local == null)
                        {
                            await Task.Delay(1).ConfigureAwait(false);
                            continue;
                        }

                        if (local._cts.IsCancellationRequested)
                            break;

                        HttpListenerContext context;
                        try
                        {
                            context = await listener.GetContextAsync().ConfigureAwait(false);
                        }
                        catch (ObjectDisposedException) { break; }
                        catch (HttpListenerException) { break; }

                        try { local.Handle(context); }
                        catch { try { context.Response.Abort(); } catch { /* ignore */ } }
                    }
                });

                var created = new RangeTestServer(listener, uri, pdf, supportRange, loop);
                Volatile.Write(ref server, created);
                return created;
            }

            private void Handle(HttpListenerContext context)
            {
                var request = context.Request;
                var response = context.Response;
                response.AddHeader("Accept-Ranges", _supportRange ? "bytes" : "none");

                if (string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 200;
                    response.ContentLength64 = _pdf.Length;
                    response.Close();
                    return;
                }

                if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 405;
                    response.Close();
                    return;
                }

                var rangeHeader = request.Headers["Range"];
                if (_supportRange && !string.IsNullOrEmpty(rangeHeader) &&
                    rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = rangeHeader.Substring("bytes=".Length).Trim();
                    var parts = spec.Split('-');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var from))
                    {
                        long to = parts[1].Length == 0 ? _pdf.Length - 1 : long.Parse(parts[1]);
                        if (from < 0) from = 0;
                        if (to >= _pdf.Length) to = _pdf.Length - 1;
                        if (from > to)
                        {
                            response.StatusCode = 416;
                            response.Close();
                            return;
                        }

                        var length = (int)(to - from + 1);
                        response.StatusCode = 206;
                        response.AddHeader("Content-Range", $"bytes {from}-{to}/{_pdf.Length}");
                        response.ContentLength64 = length;
                        response.OutputStream.Write(_pdf, (int)from, length);
                        response.Close();
                        return;
                    }
                }

                response.StatusCode = 200;
                response.ContentLength64 = _pdf.Length;
                response.OutputStream.Write(_pdf, 0, _pdf.Length);
                response.Close();
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { /* ignore */ }
                try { _listener.Close(); } catch { /* ignore */ }
                try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
                _cts.Dispose();
            }

            private static int GetFreePort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }
    }
}
