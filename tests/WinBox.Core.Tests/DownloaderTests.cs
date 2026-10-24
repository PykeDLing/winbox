using System.Net;
using WinBox.Core.Import;

namespace WinBox.Core.Tests;

[TestClass]
public sealed class DownloaderTests
{
    [TestMethod]
    public async Task DownloadThatStopsDeliveringFailsInsteadOfWaitingForever()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new Handler(() => new StreamContent(new StallingStream("first bytes"u8.ToArray()))));
            var downloader = new HttpDownloader(http, TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsExactlyAsync<TimeoutException>(() => downloader.DownloadAsync(new Uri("https://example.test/full.exe"), dir));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "full.exe")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellingIsNotReportedAsAStall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new Handler(() => new StreamContent(new StallingStream([]))));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new HttpDownloader(http, TimeSpan.FromSeconds(30)).DownloadAsync(new Uri("https://example.test/full.exe"), dir, cancellationToken: cancel.Token));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content(), RequestMessage = request });
    }

    /// <summary>Delivers its bytes, then never again: a connection that dropped without closing.</summary>
    private sealed class StallingStream(byte[] first) : Stream
    {
        private bool _delivered;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delivered && first.Length > 0)
            {
                _delivered = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
