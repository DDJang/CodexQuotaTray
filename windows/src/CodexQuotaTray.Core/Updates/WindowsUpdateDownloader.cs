using System.Security.Cryptography;

namespace CodexQuotaTray.Core.Updates;

public sealed class WindowsUpdateDownloader : IDisposable
{
    public const long MaximumInstallerBytes = 250L * 1024 * 1024;
    private const long MaximumChecksumBytes = 128 * 1024;
    private static readonly TimeSpan StaleCacheAge = TimeSpan.FromDays(7);
    private readonly HttpClient client;
    private readonly bool disposeClient;
    private readonly string cacheDirectory;

    public WindowsUpdateDownloader(string cacheDirectory, HttpClient? client = null)
    {
        this.cacheDirectory = cacheDirectory;
        this.client = client ?? CreateClient();
        disposeClient = client is null;
    }

    public Task<WindowsUpdateDownloadResult> DownloadAsync(
        WindowsUpdateRelease release,
        CancellationToken cancellationToken) =>
        DownloadAsync(release, progress: null, cancellationToken: cancellationToken);

    public async Task<WindowsUpdateDownloadResult> DownloadAsync(
        WindowsUpdateRelease release,
        IProgress<WindowsUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expectedName = $"CodexQuotaTray-{release.Version}-setup.exe";
        if (!string.Equals(release.Installer.Name, expectedName, StringComparison.Ordinal)
            || !WindowsUpdateSecurity.IsAllowedAssetUri(release.Installer.Url)
            || !string.Equals(release.Checksums.Name, "SHA256SUMS.txt", StringComparison.Ordinal)
            || !WindowsUpdateSecurity.IsAllowedAssetUri(release.Checksums.Url))
        {
            return WindowsUpdateDownloadResult.Failed("更新安装包来源或文件名无效");
        }

        var target = Path.Combine(cacheDirectory, expectedName);
        var part = target + ".part";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            CleanupStaleCache();
            progress?.Report(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Downloading));
            await DownloadFileAsync(
                release.Installer.Url,
                part,
                MaximumInstallerBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new WindowsUpdateDownloadProgress(
                WindowsUpdateDownloadPhase.Verifying,
                GetFileLength(part),
                GetFileLength(part)));
            var checksums = await DownloadTextAsync(release.Checksums.Url, MaximumChecksumBytes, cancellationToken)
                .ConfigureAwait(false);
            var expectedHash = FindExpectedHash(checksums, expectedName);
            if (expectedHash is null)
            {
                throw new InvalidDataException("SHA256SUMS.txt 中没有匹配的安装包校验值。");
            }

            var actualHash = await ComputeSha256Async(part, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("安装包 SHA-256 校验失败。");
            }

            File.Move(part, target, overwrite: true);
            return WindowsUpdateDownloadResult.Prepared(target);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(part);
            var result = cancellationToken.IsCancellationRequested
                ? WindowsUpdateDownloadResult.Cancelled()
                : WindowsUpdateDownloadResult.Failed("更新下载超时");
            progress?.Report(new WindowsUpdateDownloadProgress(
                result.WasCancelled ? WindowsUpdateDownloadPhase.Cancelled : WindowsUpdateDownloadPhase.Failed));
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            DeleteIfExists(part);
            DeleteIfExists(target);
            progress?.Report(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Failed));
            return WindowsUpdateDownloadResult.Failed(error.Message);
        }
    }

    internal static string? FindExpectedHash(string content, string fileName)
    {
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[0].Length != 64 || !IsHex(fields[0]))
            {
                continue;
            }

            var candidateName = fields[^1].TrimStart('*');
            if (string.Equals(Path.GetFileName(candidateName), fileName, StringComparison.Ordinal))
            {
                return fields[0];
            }
        }

        return null;
    }

    internal void CleanupStaleCache()
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleCacheAge;
        foreach (var file in Directory.EnumerateFiles(cacheDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task DownloadFileAsync(
        Uri uri,
        string path,
        long maximumBytes,
        IProgress<WindowsUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAssetAsync(uri, cancellationToken).ConfigureAwait(false);
        var totalBytes = response.Content.Headers.ContentLength;
        if (totalBytes is long length && length > maximumBytes)
        {
            throw new InvalidDataException("更新文件超过允许的大小限制。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long total = 0;
        progress?.Report(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Downloading, 0, totalBytes));
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("更新文件超过允许的大小限制。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Downloading, total, totalBytes));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long GetFileLength(string path) => new FileInfo(path).Length;

    private async Task<string> DownloadTextAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAssetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException("校验文件超过允许的大小限制。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("校验文件超过允许的大小限制。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return System.Text.Encoding.UTF8.GetString(destination.ToArray());
    }

    private async Task<HttpResponseMessage> SendAssetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!WindowsUpdateSecurity.IsAllowedAssetUri(uri))
        {
            throw new InvalidDataException("更新文件来源不受信任。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            || response.RequestMessage?.RequestUri is not { } finalUri
            || !WindowsUpdateSecurity.IsAllowedAssetUri(finalUri))
        {
            response.Dispose();
            throw new HttpRequestException("更新文件下载地址无效。");
        }

        return response;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool IsHex(string value) => value.All(Uri.IsHexDigit);

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexQuotaTray-Windows-Updater/1.0");
        return client;
    }

    public void Dispose()
    {
        if (disposeClient)
        {
            client.Dispose();
        }
    }
}
