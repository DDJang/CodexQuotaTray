using System.Text.Json;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Relay;

internal sealed class SnapshotStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory;

    internal SnapshotStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(this.directory);
        }
        else
        {
            Directory.CreateDirectory(this.directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public async Task<RemoteSyncSnapshot?> ReadAsync(string device, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCurrentAsync(PathFor(device), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MergeAsync(string device, RemoteSyncSnapshot snapshot, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(device);
            var existing = await LoadCurrentAsync(path, cancellationToken);
            var merged = new RemoteSyncSnapshot(
                1,
                Newer(existing?.Quota, RemoteSyncSnapshot.ProjectQuota(snapshot.Quota), value => value.GeneratedAtUtc),
                Newer(existing?.TokenUsage, snapshot.TokenUsage, value => value.GeneratedAtUtc));
            if (merged == existing || merged is { Quota: null, TokenUsage: null })
            {
                return;
            }
            await SaveAsync(path, merged, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static bool IsValid(RemoteSyncSnapshot snapshot)
    {
        var latest = DateTimeOffset.UtcNow + FutureTolerance;
        return snapshot.SchemaVersion == 1
            && (snapshot.Quota is not null || snapshot.TokenUsage is not null)
            && (snapshot.Quota is null || snapshot.Quota is
            {
                SchemaVersion: 1,
                QuotaState: "available" or "zero_windows",
                Windows.Count: <= 128,
            } quota && quota.GeneratedAtUtc > DateTimeOffset.UnixEpoch && quota.GeneratedAtUtc <= latest
                && quota.Windows.All(window => window is not null))
            && (snapshot.TokenUsage is null || snapshot.TokenUsage is
            {
                SchemaVersion: 1,
                Scope: "Local" or "Account",
                Summary: not null,
                Days.Count: <= 365,
            } usage && usage.GeneratedAtUtc > DateTimeOffset.UnixEpoch && usage.GeneratedAtUtc <= latest
                && !string.IsNullOrWhiteSpace(usage.SourceTimeZone) && usage.Days.All(day => day is not null));
    }

    private string PathFor(string device) => Path.Combine(directory, device + ".json");

    private async Task<RemoteSyncSnapshot?> LoadCurrentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        RemoteSyncSnapshot? snapshot;
        await using (var stream = File.OpenRead(path))
        {
            snapshot = await JsonSerializer.DeserializeAsync<RemoteSyncSnapshot>(
                stream, JsonSerializerOptions.Web, cancellationToken);
        }
        var cutoff = DateTimeOffset.UtcNow - Retention;
        var current = new RemoteSyncSnapshot(
            1,
            snapshot?.Quota is { } quota && quota.GeneratedAtUtc >= cutoff ? quota : null,
            snapshot?.TokenUsage is { } usage && usage.GeneratedAtUtc >= cutoff ? usage : null);
        if (current is { Quota: null, TokenUsage: null })
        {
            File.Delete(path);
            return null;
        }
        if (current != snapshot)
        {
            await SaveAsync(path, current, cancellationToken);
        }
        return current;
    }

    private static T? Newer<T>(T? existing, T? incoming, Func<T, DateTimeOffset> timestamp) where T : class
    {
        if (incoming is null || timestamp(incoming) < DateTimeOffset.UtcNow - Retention)
        {
            return existing;
        }
        return existing is null || timestamp(incoming) > timestamp(existing) ? incoming : existing;
    }

    private static async Task SaveAsync(string path, RemoteSyncSnapshot snapshot, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonSerializerOptions.Web, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (stream.Length > RemoteSyncSnapshot.MaximumBytes)
                {
                    throw new InvalidDataException("The merged snapshot exceeds the response size limit.");
                }
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
