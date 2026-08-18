using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed class TokenUsageScanner
{
    private const int ReadBufferSize = 64 * 1024;
    private const int DefaultMaximumCandidateRecordBytes = 1024 * 1024;
    private static readonly byte[] TokenCountMarker = "\"token_count\""u8.ToArray();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly int maximumCandidateRecordBytes;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly Dictionary<string, FileScanState> fileStates = new(PathComparer);
    private string? activeCodexHome;

    public TokenUsageScanner()
        : this(DefaultMaximumCandidateRecordBytes)
    {
    }

    internal TokenUsageScanner(int maximumCandidateRecordBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCandidateRecordBytes, TokenCountMarker.Length);
        this.maximumCandidateRecordBytes = maximumCandidateRecordBytes;
    }

    internal long LastBytesRead { get; private set; }

    internal int LastFilesRead { get; private set; }

    internal long TotalBytesRead { get; private set; }

    public async Task<TokenUsageSnapshot> ScanAsync(
        string? codexHome = null,
        TimeZoneInfo? sourceTimeZone = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            codexHome = ResolveCodexHome(codexHome);
            sourceTimeZone ??= TimeZoneInfo.Local;
            var utcNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, sourceTimeZone).DateTime);
            var stopwatch = Stopwatch.StartNew();
            var files = EnumerateSessionFiles(codexHome).ToArray();

            if (!PathComparer.Equals(activeCodexHome, codexHome))
            {
                fileStates.Clear();
                activeCodexHome = codexHome;
            }

            var activePaths = new HashSet<string>(PathComparer);
            var bytesRead = 0L;
            var filesRead = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                info.Refresh();
                if (!info.Exists)
                {
                    continue;
                }

                activePaths.Add(file);
                if (fileStates.TryGetValue(file, out var existing)
                    && IsUnchanged(existing, info))
                {
                    continue;
                }

                FileScanState updated;
                if (existing is not null && CanContinueFromCheckpoint(existing, info))
                {
                    updated = await AppendFileAsync(file, info, existing, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    updated = await ScanFileFromStartAsync(file, info, cancellationToken).ConfigureAwait(false);
                }

                fileStates[file] = updated;
                bytesRead += updated.BytesReadOnLastUpdate;
                filesRead++;
            }

            foreach (var removed in fileStates.Keys.Where(path => !activePaths.Contains(path)).ToArray())
            {
                fileStates.Remove(removed);
            }

            LastBytesRead = bytesRead;
            LastFilesRead = filesRead;
            TotalBytesRead += bytesRead;

            var days = new Dictionary<DateOnly, DayAccumulator>();
            var seenEvents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (!fileStates.TryGetValue(file, out var state))
                {
                    continue;
                }

                foreach (var value in state.Events)
                {
                    if (!seenEvents.Add(value.Key))
                    {
                        continue;
                    }

                    var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value.Timestamp, sourceTimeZone).DateTime);
                    if (!days.TryGetValue(localDate, out var day))
                    {
                        day = new DayAccumulator();
                        days.Add(localDate, day);
                    }

                    day.Add(value.Delta);
                }
            }

            var allDays = days.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToDay(pair.Key)).ToArray();
            var active = allDays.Where(day => day.TotalTokens > 0).ToArray();
            var peak = active.OrderByDescending(day => day.TotalTokens).ThenBy(day => day.Date).FirstOrDefault();
            var activeDates = active.Select(day => day.Date).ToHashSet();
            var summary = new TokenUsageSummary(
                Sum(allDays.Where(day => day.Date == today).Select(day => day.TotalTokens)),
                Sum(allDays.Where(day => day.Date >= today.AddDays(-6) && day.Date <= today).Select(day => day.TotalTokens)),
                Sum(allDays.Where(day => day.Date >= today.AddDays(-29) && day.Date <= today).Select(day => day.TotalTokens)),
                Sum(allDays.Select(day => day.TotalTokens)),
                peak?.TotalTokens ?? 0,
                peak?.Date,
                active.Length,
                CurrentStreak(activeDates, today),
                LongestStreak(activeDates));
            var recent = allDays.Where(day => day.Date >= today.AddDays(-364) && day.Date <= today).ToArray();
            stopwatch.Stop();
            return new TokenUsageSnapshot(
                1,
                utcNow,
                TimeZoneName(sourceTimeZone),
                summary,
                recent,
                activePaths.Count,
                stopwatch.ElapsedMilliseconds,
                active.FirstOrDefault()?.Date,
                active.LastOrDefault()?.Date);
        }
        finally
        {
            scanGate.Release();
        }
    }

    internal static IEnumerable<string> EnumerateSessionFiles(string codexHome)
    {
        var sessions = Path.Combine(codexHome, "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var file in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories)
                         .Where(file => IsPartitionedSessionPath(sessions, file)))
            {
                yield return file;
            }
        }

        var archived = Path.Combine(codexHome, "archived_sessions");
        if (Directory.Exists(archived))
        {
            foreach (var file in Directory.EnumerateFiles(archived, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }
    }

    private static bool IsPartitionedSessionPath(string sessionsRoot, string file)
    {
        var parts = Path.GetRelativePath(sessionsRoot, file).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return parts.Length == 4
            && parts[0].Length == 4 && parts[0].All(char.IsDigit)
            && parts[1].Length == 2 && parts[1].All(char.IsDigit)
            && parts[2].Length == 2 && parts[2].All(char.IsDigit);
    }

    private static bool IsUnchanged(FileScanState state, FileInfo info) =>
        state.ObservedLength == info.Length
        && state.LastWriteTimeUtc == info.LastWriteTimeUtc
        && state.CreationTimeUtc == info.CreationTimeUtc;

    private static bool CanContinueFromCheckpoint(FileScanState state, FileInfo info) =>
        state.CreationTimeUtc == info.CreationTimeUtc
        && info.Length > state.ObservedLength
        && info.Length >= state.ProcessedLength;

    private async Task<FileScanState> ScanFileFromStartAsync(
        string path,
        FileInfo info,
        CancellationToken cancellationToken)
    {
        var result = await ScanSegmentAsync(path, 0, previousTotal: null, cancellationToken).ConfigureAwait(false);
        return CreateState(info, result, result.Events);
    }

    private async Task<FileScanState> AppendFileAsync(
        string path,
        FileInfo info,
        FileScanState existing,
        CancellationToken cancellationToken)
    {
        var result = await ScanSegmentAsync(path, existing.ProcessedLength, existing.PreviousTotal, cancellationToken).ConfigureAwait(false);
        var events = new List<TokenUsageEvent>(existing.Events.Count + result.Events.Count);
        events.AddRange(existing.Events);
        events.AddRange(result.Events);
        return CreateState(info, result, events);
    }

    private static FileScanState CreateState(
        FileInfo beforeScan,
        FileScanResult result,
        IReadOnlyList<TokenUsageEvent> events)
    {
        var afterScan = new FileInfo(beforeScan.FullName);
        afterScan.Refresh();
        var stableLastWrite = afterScan.Exists && afterScan.Length == result.ObservedLength
            ? afterScan.LastWriteTimeUtc
            : DateTime.MinValue;
        return new FileScanState(
            result.ObservedLength,
            result.ProcessedLength,
            stableLastWrite,
            beforeScan.CreationTimeUtc,
            result.PreviousTotal,
            events,
            result.BytesRead);
    }

    private async Task<FileScanResult> ScanSegmentAsync(
        string path,
        long startOffset,
        TokenCounters? previousTotal,
        CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        var lineBuffer = ArrayPool<byte>.Shared.Rent(maximumCandidateRecordBytes);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(startOffset, SeekOrigin.Begin);

            var events = new List<TokenUsageEvent>();
            var buffered = 0;
            var markerMatched = 0;
            var containsMarker = false;
            var overflow = false;
            var lineHasContent = false;
            var absoluteOffset = startOffset;
            var processedLength = startOffset;
            var lineStartOffset = startOffset;
            var bytesRead = 0L;
            int count;
            while ((count = await stream.ReadAsync(readBuffer.AsMemory(0, ReadBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                bytesRead += count;
                for (var index = 0; index < count; index++)
                {
                    var value = readBuffer[index];
                    absoluteOffset++;
                    if (value == (byte)'\n')
                    {
                        _ = ProcessLine(
                            lineBuffer,
                            buffered,
                            containsMarker,
                            overflow,
                            lineStartOffset == 0,
                            ref previousTotal,
                            events);
                        processedLength = absoluteOffset;
                        lineStartOffset = absoluteOffset;
                        buffered = 0;
                        markerMatched = 0;
                        containsMarker = false;
                        overflow = false;
                        lineHasContent = false;
                        continue;
                    }

                    lineHasContent = true;
                    if (buffered < maximumCandidateRecordBytes)
                    {
                        lineBuffer[buffered++] = value;
                    }
                    else
                    {
                        overflow = true;
                    }

                    if (!containsMarker)
                    {
                        markerMatched = AdvanceMarker(markerMatched, value);
                        containsMarker = markerMatched == TokenCountMarker.Length;
                    }
                }
            }

            if (lineHasContent)
            {
                var disposition = ProcessLine(
                    lineBuffer,
                    buffered,
                    containsMarker,
                    overflow,
                    lineStartOffset == 0,
                    ref previousTotal,
                    events);
                if (disposition is LineDisposition.NonCandidate or LineDisposition.ParsedCandidate)
                {
                    processedLength = absoluteOffset;
                }
            }

            return new FileScanResult(
                absoluteOffset,
                processedLength,
                previousTotal,
                events,
                bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lineBuffer);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static int AdvanceMarker(int matched, byte value)
    {
        if (value == TokenCountMarker[matched])
        {
            return matched + 1;
        }

        return value == TokenCountMarker[0] ? 1 : 0;
    }

    private static LineDisposition ProcessLine(
        byte[] buffer,
        int length,
        bool containsMarker,
        bool overflow,
        bool firstLine,
        ref TokenCounters? previousTotal,
        List<TokenUsageEvent> events)
    {
        if (!containsMarker)
        {
            return LineDisposition.NonCandidate;
        }

        if (overflow)
        {
            return LineDisposition.OversizedCandidate;
        }

        var offset = firstLine
            && length >= 3
            && buffer[0] == 0xEF
            && buffer[1] == 0xBB
            && buffer[2] == 0xBF
                ? 3
                : 0;
        if (length > offset && buffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, offset, length - offset));
            var root = document.RootElement;
            if (!Text(root, "type").Equals("event_msg", StringComparison.Ordinal)
                || !root.TryGetProperty("payload", out var payload)
                || !Text(payload, "type").Equals("token_count", StringComparison.Ordinal)
                || !root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return LineDisposition.InvalidCandidate;
            }

            var total = Counters(info, "total_token_usage");
            var last = Counters(info, "last_token_usage");
            if (total is null && last is null)
            {
                return LineDisposition.InvalidCandidate;
            }

            var eventKey = EventKey(timestampElement.GetString()!, total, last);
            var delta = last ?? Delta(previousTotal, total!.Value);
            if (total is not null)
            {
                previousTotal = total;
            }

            events.Add(new TokenUsageEvent(eventKey, timestamp, delta));
            return LineDisposition.ParsedCandidate;
        }
        catch (JsonException)
        {
            return LineDisposition.InvalidCandidate;
        }
    }

    private static TokenCounters? Counters(JsonElement info, string name)
    {
        if (!info.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = Long(value, "input_tokens");
        var output = Long(value, "output_tokens");
        var total = Long(value, "total_tokens") ?? (input is not null && output is not null ? Add(input.Value, output.Value) : null);
        if (total is null)
        {
            return null;
        }

        return new TokenCounters(
            Math.Max(0, total.Value),
            NonNegative(input),
            NonNegative(Long(value, "cached_input_tokens") ?? Long(value, "cache_read_input_tokens")),
            NonNegative(output),
            NonNegative(Long(value, "reasoning_output_tokens")));
    }

    private static TokenCounters Delta(TokenCounters? previous, TokenCounters current)
    {
        if (previous is null)
        {
            return current;
        }

        return new TokenCounters(
            Difference(previous.Value.Total, current.Total),
            Difference(previous.Value.Input, current.Input),
            Difference(previous.Value.Cached, current.Cached),
            Difference(previous.Value.Output, current.Output),
            Difference(previous.Value.Reasoning, current.Reasoning));
    }

    private static long Difference(long previous, long current) => current >= previous ? current - previous : current;

    private static long? Difference(long? previous, long? current) =>
        previous is not null && current is not null ? Difference(previous.Value, current.Value) : null;

    private static string EventKey(string timestamp, TokenCounters? total, TokenCounters? last)
    {
        var value = string.Create(CultureInfo.InvariantCulture, $"{timestamp}|{total}|{last}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static long? Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) ? result : null;

    private static long? NonNegative(long? value) => value is null ? null : Math.Max(0, value.Value);

    private static long Sum(IEnumerable<long> values)
    {
        var result = 0L;
        foreach (var value in values)
        {
            result = Add(result, value);
        }

        return result;
    }

    private static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static int CurrentStreak(HashSet<DateOnly> active, DateOnly today)
    {
        var date = active.Contains(today) ? today : today.AddDays(-1);
        var count = 0;
        while (active.Contains(date))
        {
            count++;
            date = date.AddDays(-1);
        }

        return count;
    }

    private static int LongestStreak(HashSet<DateOnly> active)
    {
        var longest = 0;
        var current = 0;
        DateOnly? previous = null;
        foreach (var date in active.Order())
        {
            current = previous is not null && date == previous.Value.AddDays(1) ? current + 1 : 1;
            longest = Math.Max(longest, current);
            previous = date;
        }

        return longest;
    }

    private static string ResolveCodexHome(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return Path.GetFullPath(value);
        }

        var environment = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(environment)
            ? Path.GetFullPath(environment)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static string TimeZoneName(TimeZoneInfo value) =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(value.Id, out var iana) ? iana : value.Id;

    private enum LineDisposition
    {
        NonCandidate,
        ParsedCandidate,
        InvalidCandidate,
        OversizedCandidate,
    }

    private readonly record struct TokenCounters(long Total, long? Input, long? Cached, long? Output, long? Reasoning);

    private sealed record TokenUsageEvent(string Key, DateTimeOffset Timestamp, TokenCounters Delta);

    private sealed record FileScanState(
        long ObservedLength,
        long ProcessedLength,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc,
        TokenCounters? PreviousTotal,
        IReadOnlyList<TokenUsageEvent> Events,
        long BytesReadOnLastUpdate);

    private sealed record FileScanResult(
        long ObservedLength,
        long ProcessedLength,
        TokenCounters? PreviousTotal,
        IReadOnlyList<TokenUsageEvent> Events,
        long BytesRead);

    private sealed class DayAccumulator
    {
        private long total;
        private long input;
        private long cached;
        private long output;
        private long reasoning;
        private bool inputAvailable = true;
        private bool cachedAvailable = true;
        private bool outputAvailable = true;
        private bool reasoningAvailable = true;

        internal void Add(TokenCounters value)
        {
            total = TokenUsageScanner.Add(total, value.Total);
            inputAvailable &= AddOptional(ref input, value.Input);
            cachedAvailable &= AddOptional(ref cached, value.Cached);
            outputAvailable &= AddOptional(ref output, value.Output);
            reasoningAvailable &= AddOptional(ref reasoning, value.Reasoning);
        }

        internal TokenUsageDay ToDay(DateOnly date) => new(
            date,
            total,
            inputAvailable ? input : null,
            cachedAvailable ? cached : null,
            outputAvailable ? output : null,
            reasoningAvailable ? reasoning : null);

        private static bool AddOptional(ref long target, long? value)
        {
            if (value is null)
            {
                return false;
            }

            target = TokenUsageScanner.Add(target, value.Value);
            return true;
        }
    }
}
