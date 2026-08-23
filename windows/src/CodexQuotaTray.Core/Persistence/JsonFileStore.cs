using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexQuotaTray.Core.Persistence;

public sealed class JsonFileStore
{
    public const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<T?> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        var info = new FileInfo(path);
        if (info.Length > MaximumBytes)
        {
            throw new InvalidDataException("The persisted document exceeds the safe size limit.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A data directory is required.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        var backup = path + ".bak";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumBytes)
                {
                    throw new InvalidDataException("The persisted document exceeds the safe size limit.");
                }
            }

            if (File.Exists(path))
            {
                File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<bool> SaveWithCommitAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken,
        SemaphoreSlim commitGate,
        Func<bool> canCommit,
        Action? onCommitted = null)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("A data directory is required.");
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = path + ".bak";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumBytes)
                {
                    throw new InvalidDataException("The persisted document exceeds the safe size limit.");
                }
            }

            await commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!canCommit())
                {
                    return false;
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, path);
                }

                onCommitted?.Invoke();
                return true;
            }
            finally
            {
                commitGate.Release();
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

public sealed class PreviewDataPaths
{
    public PreviewDataPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaTray-WinUI-Preview");
    }

    public string Root { get; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string QuotaCache => Path.Combine(Root, "quota-cache.json");
    public string TokenUsageCache => Path.Combine(Root, "token-usage-cache.json");
    public string TokenUsageDatabase => Path.Combine(Root, "token-usage.sqlite3");
    public string AlertState => Path.Combine(Root, "alert-state.json");
    public string TokenSyncSettings => Path.Combine(Root, "token-sync.json");
    public string OAuthCredentials => Path.Combine(Root, "oauth-credentials.bin");

    public string QuotaCacheFor(QuotaDataSource source) => source switch
    {
        QuotaDataSource.CodexCli => QuotaCache,
        QuotaDataSource.OAuth => Path.Combine(Root, "quota-cache-oauth.json"),
        _ => QuotaCache,
    };

    public string TokenUsageCacheFor(TokenUsageDataSource source) => source switch
    {
        TokenUsageDataSource.Local => TokenUsageCache,
        TokenUsageDataSource.CodexCli => Path.Combine(Root, "token-usage-cache-codex-cli.json"),
        TokenUsageDataSource.OAuth => Path.Combine(Root, "token-usage-cache-oauth.json"),
        _ => TokenUsageCache,
    };
}
