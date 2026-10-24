using System.Security.Cryptography;

namespace WinBox.Core.Import;

public interface IDownloader
{
    /// <summary>Downloads <paramref name="url"/> into <paramref name="directory"/>, following redirects, and returns the file path.</summary>
    Task<string> DownloadAsync(Uri url, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record DownloadProgress(long Received, long? Total);

/// <summary>
/// Downloads with HttpClient. The file name comes from the final (post-redirect) URL, and the file only
/// appears under that name once it is complete.
/// </summary>
/// <param name="stallTimeout">
/// How long the body may deliver nothing before the download fails with <see cref="TimeoutException"/>.
/// HttpClient's own timeout stops counting once the headers are in, so without this a dropped connection
/// would leave the download waiting forever.
/// </param>
public sealed class HttpDownloader(HttpClient http, TimeSpan? stallTimeout = null) : IDownloader
{
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _stallTimeout = stallTimeout ?? DefaultStallTimeout;

    public async Task<string> DownloadAsync(Uri url, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri ?? url;
        var name = Uri.UnescapeDataString(Path.GetFileName(finalUrl.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(finalUrl.ToString())))[..16] + ".bin";

        var target = Path.Combine(directory, name);
        var total = response.Content.Headers.ContentLength;
        if (File.Exists(target) && total is not null && new FileInfo(target).Length == total)
            return target;

        var partial = target + ".partial";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            while (true)
            {
                stall.CancelAfter(_stallTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No data received for {_stallTimeout.TotalSeconds:0} seconds.");
                }
                if (read == 0)
                    break;
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }
}
