using System.Globalization;
using System.Text;

namespace CodexQuotaTray.Core.Persistence;

public sealed record PreviousCrashInfo(string LogPath, string NoticeId);

public sealed class CrashSessionLog
{
    private readonly object syncRoot = new();
    private readonly string markerPath;
    private readonly string pendingNoticePath;
    private readonly string acknowledgedNoticePath;
    private string? sessionId;

    public CrashSessionLog(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        LogPath = Path.Combine(dataDirectory, "crash.log");
        markerPath = Path.Combine(dataDirectory, "running.marker");
        pendingNoticePath = Path.Combine(dataDirectory, "crash-pending.marker");
        acknowledgedNoticePath = Path.Combine(dataDirectory, "crash-acknowledged.marker");
    }

    public string LogPath { get; }

    public PreviousCrashInfo? StartSession()
    {
        lock (syncRoot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                var previousSessionId = File.Exists(markerPath)
                    ? File.ReadAllText(markerPath, Encoding.UTF8).Trim()
                    : null;
                var pendingNoticeId = ReadMarker(pendingNoticePath);
                var acknowledgedNoticeId = ReadMarker(acknowledgedNoticePath);
                var hasPendingNotice = !string.IsNullOrWhiteSpace(pendingNoticeId)
                    && !string.Equals(pendingNoticeId, acknowledgedNoticeId, StringComparison.Ordinal);
                if (!hasPendingNotice
                    && !string.IsNullOrWhiteSpace(pendingNoticeId)
                    && string.Equals(pendingNoticeId, acknowledgedNoticeId, StringComparison.Ordinal))
                {
                    TryDelete(pendingNoticePath);
                    pendingNoticeId = null;
                }

                if (!string.IsNullOrWhiteSpace(previousSessionId)
                    && !hasPendingNotice
                    && !LogMatchesSession(previousSessionId))
                {
                    WriteUnexpectedTermination(previousSessionId);
                    WritePendingNotice(previousSessionId);
                    pendingNoticeId = previousSessionId;
                    hasPendingNotice = true;
                }

                sessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                File.WriteAllText(markerPath, sessionId, Encoding.UTF8);
                return hasPendingNotice ? new PreviousCrashInfo(LogPath, pendingNoticeId!) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                sessionId = null;
                return null;
            }
        }
    }

    public void Record(Exception error, string source)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock (syncRoot)
        {
            if (sessionId is null)
            {
                return;
            }

            try
            {
                var text = string.Join(
                    Environment.NewLine,
                    $"Session: {sessionId}",
                    $"TimestampUtc: {DateTimeOffset.UtcNow:O}",
                    $"Source: {source}",
                    $"Exception: {error.GetType().FullName}",
                    $"HResult: 0x{error.HResult:X8}",
                    $"Message: {error.Message}",
                    "StackTrace:",
                    error.StackTrace ?? "(unavailable)");
                File.WriteAllText(LogPath, text, Encoding.UTF8);
                WritePendingNotice(sessionId);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void CompleteSession()
    {
        lock (syncRoot)
        {
            if (sessionId is null)
            {
                return;
            }

            try
            {
                File.Delete(markerPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }

            sessionId = null;
        }
    }

    /// <summary>
    /// Marks a confirmed operating-system session end as an expected termination.
    /// This intentionally performs only the synchronous marker cleanup needed to
    /// prevent a cancelled or time-limited shutdown from being reported as a crash.
    /// </summary>
    public void MarkExpectedTermination() => CompleteSession();

    public bool AcknowledgePreviousCrash(PreviousCrashInfo crashInfo)
    {
        ArgumentNullException.ThrowIfNull(crashInfo);

        lock (syncRoot)
        {
            try
            {
                var pendingNoticeId = ReadMarker(pendingNoticePath);
                if (!string.IsNullOrWhiteSpace(pendingNoticeId)
                    && !string.Equals(pendingNoticeId, crashInfo.NoticeId, StringComparison.Ordinal))
                {
                    return false;
                }

                WriteMarkerAtomically(acknowledgedNoticePath, crashInfo.NoticeId);
                if (string.Equals(pendingNoticeId, crashInfo.NoticeId, StringComparison.Ordinal))
                {
                    TryDelete(pendingNoticePath);
                }

                return string.Equals(
                    ReadMarker(acknowledgedNoticePath),
                    crashInfo.NoticeId,
                    StringComparison.Ordinal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static string? ReadMarker(string path) => File.Exists(path)
        ? File.ReadAllText(path, Encoding.UTF8).Trim()
        : null;

    private bool LogMatchesSession(string expectedSessionId)
    {
        if (!File.Exists(LogPath))
        {
            return false;
        }

        using var reader = new StreamReader(LogPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return string.Equals(reader.ReadLine(), $"Session: {expectedSessionId}", StringComparison.Ordinal);
    }

    private void WriteUnexpectedTermination(string previousSessionId)
    {
        var text = string.Join(
            Environment.NewLine,
            $"Session: {previousSessionId}",
            $"TimestampUtc: {DateTimeOffset.UtcNow:O}",
            "Source: ProcessLifecycle",
            "Reason: The previous process ended without completing normal shutdown.");
        File.WriteAllText(LogPath, text, Encoding.UTF8);
    }

    private void WritePendingNotice(string crashSessionId) =>
        WriteMarkerAtomically(pendingNoticePath, crashSessionId);

    private static void WriteMarkerAtomically(string path, string value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, value, Encoding.UTF8);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
