using System.Security.Cryptography;
using System.Text.Json;
using CodexQuotaTray.Core.Auth;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed class RemoteSyncConfiguration
{
    public RemoteSyncConfiguration(string endpoint, string uploadKey, bool enabled = true)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("请输入 HTTPS 服务器地址，例如 https://sync.example.com。");
        }

        uploadKey = uploadKey.Trim();
        if (!IsValidKey(uploadKey))
        {
            throw new ArgumentException("上传密钥须为 64 位十六进制字符。");
        }

        Origin = uri;
        UploadKey = uploadKey;
        Enabled = enabled;
    }

    public Uri Origin { get; }
    public string UploadKey { get; }
    public bool Enabled { get; }

    public static bool IsValidKey(string key) => key.Length == 64 && key.All(Uri.IsHexDigit);
}

public sealed class RemoteSyncConfigurationStore(string path)
{
    public async Task<(RemoteSyncConfiguration? Configuration, DateTimeOffset? LastSuccessfulUploadUtc)> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return (null, null);
        }

        if (new FileInfo(path).Length > 4096)
        {
            throw new InvalidDataException("远程同步配置过大。");
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var clear = CurrentUserDpapi.Unprotect(protectedBytes);
        try
        {
            var config = JsonSerializer.Deserialize<RemoteSyncConfigurationData>(clear)
                ?? throw new InvalidDataException("远程同步配置无效。");
            var configuration = new RemoteSyncConfiguration(config.Endpoint, config.UploadKey, config.Enabled ?? true);
            return (configuration, config.LastSuccessfulUploadUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task SaveAsync(
        RemoteSyncConfiguration? configuration,
        DateTimeOffset? lastSuccessfulUploadUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration is null)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteSyncConfigurationData(
                configuration.Origin.AbsoluteUri, configuration.UploadKey, configuration.Enabled, lastSuccessfulUploadUtc));
        var temporary = path + ".tmp";
        try
        {
            var protectedBytes = CurrentUserDpapi.Protect(clear);
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            File.Delete(temporary);
        }
    }

    private sealed record RemoteSyncConfigurationData(
        string Endpoint, string UploadKey, bool? Enabled, DateTimeOffset? LastSuccessfulUploadUtc);
}
