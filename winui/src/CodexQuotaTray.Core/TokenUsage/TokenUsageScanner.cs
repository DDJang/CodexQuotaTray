using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed class TokenUsageScanner
{
    public async Task<TokenUsageSnapshot> ScanAsync(
        string? codexHome = null,
        TimeZoneInfo? sourceTimeZone = null,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        codexHome = ResolveCodexHome(codexHome);
        sourceTimeZone ??= TimeZoneInfo.Local;
        var utcNow = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, sourceTimeZone).DateTime);
        var stopwatch = Stopwatch.StartNew();
        var files = EnumerateSessionFiles(codexHome).ToArray();
        var days = new Dictionary<DateOnly, DayAccumulator>();
        var seenEvents = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScanFileAsync(file, sourceTimeZone, days, seenEvents, cancellationToken).ConfigureAwait(false);
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
            files.Length,
            stopwatch.ElapsedMilliseconds,
            active.FirstOrDefault()?.Date,
            active.LastOrDefault()?.Date);
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

    private static async Task ScanFileAsync(
        string path,
        TimeZoneInfo timeZone,
        Dictionary<DateOnly, DayAccumulator> days,
        HashSet<string> seenEvents,
        CancellationToken cancellationToken)
    {
        TokenCounters? previousTotal = null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 16 * 1024);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
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
                    continue;
                }

                var total = Counters(info, "total_token_usage");
                var last = Counters(info, "last_token_usage");
                if (total is null && last is null)
                {
                    continue;
                }

                var eventKey = EventKey(timestampElement.GetString()!, total, last);
                var delta = last ?? Delta(previousTotal, total!);
                if (total is not null)
                {
                    previousTotal = total;
                }

                if (!seenEvents.Add(eventKey))
                {
                    continue;
                }

                var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, timeZone).DateTime);
                if (!days.TryGetValue(localDate, out var day))
                {
                    day = new DayAccumulator();
                    days.Add(localDate, day);
                }

                day.Add(delta);
            }
            catch (JsonException)
            {
                // One malformed JSONL record must not invalidate the rest of the corpus.
            }
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
            Difference(previous.Total, current.Total),
            Difference(previous.Input, current.Input),
            Difference(previous.Cached, current.Cached),
            Difference(previous.Output, current.Output),
            Difference(previous.Reasoning, current.Reasoning));
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

    private sealed record TokenCounters(long Total, long? Input, long? Cached, long? Output, long? Reasoning);

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
