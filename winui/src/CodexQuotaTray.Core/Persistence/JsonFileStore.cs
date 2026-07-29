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
    public string AlertState => Path.Combine(Root, "alert-state.json");
}
