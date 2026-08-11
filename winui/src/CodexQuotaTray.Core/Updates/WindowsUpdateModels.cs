using System.Globalization;

namespace CodexQuotaTray.Core.Updates;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length > 1 && part[0] == '0')
            {
                return false;
            }

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])
                || numbers[index] < 0)
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

public enum WindowsUpdateCheckReason
{
    Automatic,
    Manual,
}

public enum WindowsUpdateCheckStatus
{
    NotChecked,
    Checking,
    Skipped,
    Disabled,
    UpToDate,
    Available,
    NoRelease,
    Failed,
}

public sealed record WindowsUpdateAsset(string Name, Uri Url, long? Size = null);

public sealed record WindowsUpdateRelease(
    string TagName,
    SemanticVersion Version,
    string Name,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    WindowsUpdateAsset Installer,
    WindowsUpdateAsset Checksums);

public sealed record WindowsUpdateCheckResult(
    WindowsUpdateCheckStatus Status,
    WindowsUpdateRelease? Release,
    string? ErrorMessage,
    DateTimeOffset? CheckedAtUtc)
{
    public bool HasUpdate => Status == WindowsUpdateCheckStatus.Available && Release is not null;

    public static WindowsUpdateCheckResult NotChecked { get; } = new(
        WindowsUpdateCheckStatus.NotChecked,
        null,
        null,
        null);
}

public sealed record WindowsUpdateDownloadResult(
    bool Succeeded,
    string? InstallerPath,
    string? ErrorMessage)
{
    public static WindowsUpdateDownloadResult Failed(string message) => new(false, null, message);

    public static WindowsUpdateDownloadResult Prepared(string path) => new(true, path, null);
}

public sealed record WindowsUpdateState(
    bool AutomaticChecksEnabled = true,
    bool UpdateRemindersEnabled = true,
    DateTimeOffset? LastAttemptUtc = null,
    DateTimeOffset? LastSuccessfulCheckUtc = null,
    string? LastNotifiedVersion = null);

public interface IWindowsUpdateReleaseProvider
{
    Task<WindowsUpdateRelease?> GetLatestAsync(CancellationToken cancellationToken);
}

public interface IWindowsUpdateStateStore
{
    Task<WindowsUpdateState?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(WindowsUpdateState state, CancellationToken cancellationToken);
}

public interface IUpdateClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemUpdateClock : IUpdateClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IWindowsUpdateController
{
    bool IsProduction { get; }

    bool AutomaticChecksEnabled { get; }

    bool UpdateRemindersEnabled { get; }

    DateTimeOffset? LastAttemptUtc { get; }

    DateTimeOffset? LastSuccessfulCheckUtc { get; }

    WindowsUpdateCheckResult CurrentResult { get; }

    event EventHandler? Changed;

    Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task SetUpdateRemindersEnabledAsync(bool enabled, CancellationToken cancellationToken);

    Task<WindowsUpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken);

    Task<WindowsUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken);

    Task<bool> InstallPreparedAsync(CancellationToken cancellationToken);
}
