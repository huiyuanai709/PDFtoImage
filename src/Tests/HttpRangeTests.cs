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
    public class HttpRangeTests : TestBase
    {
        [TestMethod]
        public async Task OpenNonLinearizedPdfRendersWithoutFullDownload()
        {
            var pdfBytes = ReadAsset("Wikimedia_Commons_web.pdf");
            Assert.IsGreaterThan(1_000_000, pdfBytes.Length, "Fixture should be large enough to show partial download.");

            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);
            using var stream = await PdfHttpRangeStream.OpenAsync(server.Uri, options: new PdfHttpRangeOptions
            {
                HeadProbeBytes = 64 * 1024,
                TailProbeBytes = 256 * 1024,
                MinRangeRequestBytes = 64 * 1024,
            });

            Assert.IsTrue(stream.SupportsRange);
            Assert.AreEqual(false, stream.IsLinearized);
            Assert.IsLessThan(pdfBytes.Length, stream.BytesDownloaded);

            using var session = PdfSession.Open(stream, leaveOpen: true);
            Assert.IsGreaterThan(0, session.PageCount);

            using var bitmap = session.Render(0, new RenderOptions(Dpi: 72));
            Assert.IsGreaterThan(0, bitmap.ByteCount);

            Assert.IsLessThan(pdfBytes.Length, stream.BytesDownloaded,
                $"Expected a partial download, but got {stream.BytesDownloaded} of {pdfBytes.Length} bytes.");
            Assert.IsGreaterThan(0, stream.RangeRequestCount);
            Assert.IsTrue(server.RangeRequestCount >= 1);
        }

        [TestMethod]
        public async Task OpenLinearizedPdfDetectsLinearizedFlag()
        {
            var pdfBytes = ReadAsset("hundesteuer-anmeldung.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);
            using var stream = await PdfHttpRangeStream.OpenAsync(server.Uri, options: new PdfHttpRangeOptions
            {
                HeadProbeBytes = 64 * 1024,
                TailProbeBytes = 64 * 1024,
            });

            Assert.IsTrue(stream.SupportsRange);
            Assert.AreEqual(true, stream.IsLinearized);
            Assert.IsLessThan(pdfBytes.Length, stream.BytesDownloaded,
                "A linearized open should only need the head probe before rendering.");

            using var session = PdfSession.Open(stream, leaveOpen: true);
            using var bitmap = session.Render(0, new RenderOptions(Dpi: 72));
            Assert.IsGreaterThan(0, bitmap.ByteCount);
        }

        [TestMethod]
        public async Task OpenSessionAsyncConvenienceWorks()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);

            using var session = await PdfHttpRangeStream.OpenSessionAsync(server.Uri);
            Assert.AreEqual(1, session.PageCount);
            using var bitmap = session.Render(0, new RenderOptions(Dpi: 72));
            Assert.IsGreaterThan(0, bitmap.ByteCount);
        }

        [TestMethod]
        public async Task FullDownloadFallbackWhenRangeUnsupported()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: false);
            using var stream = await PdfHttpRangeStream.OpenAsync(server.Uri, options: new PdfHttpRangeOptions
            {
                AllowFullDownloadFallback = true,
            });

            Assert.IsFalse(stream.SupportsRange);
            Assert.AreEqual(pdfBytes.Length, stream.BytesDownloaded);

            using var session = PdfSession.Open(stream, leaveOpen: true);
            Assert.AreEqual(1, session.PageCount);
        }

        [TestMethod]
        public async Task DisallowFallbackThrowsWhenRangeUnsupported()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: false);

            await Assert.ThrowsExactlyAsync<PdfHttpRangeException>(async () =>
            {
                _ = await PdfHttpRangeStream.OpenAsync(server.Uri, options: new PdfHttpRangeOptions
                {
                    AllowFullDownloadFallback = false,
                });
            });
        }

        [TestMethod]
        public async Task MaxDownloadBytesIsEnforced()
        {
            var pdfBytes = ReadAsset("Wikimedia_Commons_web.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);

            await Assert.ThrowsExactlyAsync<PdfHttpRangeDownloadLimitException>(async () =>
            {
                _ = await PdfHttpRangeStream.OpenAsync(server.Uri, options: new PdfHttpRangeOptions
                {
                    HeadProbeBytes = 64 * 1024,
                    TailProbeBytes = 256 * 1024,
                    MaxDownloadBytes = 8 * 1024,
                });
            });
        }

        [TestMethod]
        public async Task HeadThenTailProbeOrderForNonLinearized()
        {
            var pdfBytes = ReadAsset("SocialPreview.pdf");
            using var server = RangeTestServer.Start(pdfBytes, supportRange: true);
            using var stream = await PdfHttpRangeStream.OpenAsync(server.Uri, options: new PdfHttpRangeOptions
            {
                HeadProbeBytes = 4096,
                TailProbeBytes = 4096,
                MinRangeRequestBytes = 4096,
            });

            Assert.AreEqual(false, stream.IsLinearized);
            Assert.IsTrue(stream.RangeRequestCount >= 2);
            Assert.IsTrue(server.RequestedRanges.Any(r => r.From == 0), "Expected a head probe starting at byte 0.");
            Assert.IsTrue(server.RequestedRanges.Any(r => r.From > 0 || r.To >= pdfBytes.Length - 1),
                "Expected a tail (or additional) range after the head probe.");
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
            private readonly List<RangeRequest> _ranges = new();
            private readonly object _gate = new();

            private RangeTestServer(HttpListener listener, Uri uri, byte[] pdf, bool supportRange, Task loop)
            {
                _listener = listener;
                Uri = uri;
                _pdf = pdf;
                _supportRange = supportRange;
                _loop = loop;
            }

            public Uri Uri { get; }

            public int RangeRequestCount
            {
                get { lock (_gate) return _ranges.Count; }
            }

            public IReadOnlyList<RangeRequest> RequestedRanges
            {
                get { lock (_gate) return _ranges.ToList(); }
            }

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
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch (HttpListenerException)
                        {
                            break;
                        }

                        try
                        {
                            local.Handle(context);
                        }
                        catch
                        {
                            try { context.Response.Abort(); } catch { /* ignore */ }
                        }
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
                        if (from < 0)
                            from = 0;
                        if (to >= _pdf.Length)
                            to = _pdf.Length - 1;

                        if (from > to)
                        {
                            response.StatusCode = 416;
                            response.Close();
                            return;
                        }

                        lock (_gate)
                            _ranges.Add(new RangeRequest(from, to));

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

            public readonly struct RangeRequest
            {
                public RangeRequest(long from, long to)
                {
                    From = from;
                    To = to;
                }

                public long From { get; }
                public long To { get; }
            }
        }
    }
}
