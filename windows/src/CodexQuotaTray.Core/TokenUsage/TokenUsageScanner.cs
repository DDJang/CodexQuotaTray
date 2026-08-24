using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed class TokenUsageScanner
{
    private const int ReadBufferSize = 64 * 1024;
    private const int DefaultMaximumCandidateRecordBytes = 1024 * 1024;
    private static readonly byte[][] RelevantMarkers =
    [
        "\"token_count\""u8.ToArray(),
        "\"session_meta\""u8.ToArray(),
        "\"task_started\""u8.ToArray(),
    ];

    private readonly string? databasePath;
    private readonly int maximumCandidateRecordBytes;
    private readonly SemaphoreSlim scanGate = new(1, 1);

    public TokenUsageScanner() : this(null, DefaultMaximumCandidateRecordBytes) { }

    public TokenUsageScanner(string databasePath) : this(databasePath, DefaultMaximumCandidateRecordBytes) { }

    internal TokenUsageScanner(int maximumCandidateRecordBytes) : this(null, maximumCandidateRecordBytes) { }

    internal TokenUsageScanner(string? databasePath, int maximumCandidateRecordBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCandidateRecordBytes, RelevantMarkers.Max(value => value.Length));
        this.databasePath = string.IsNullOrWhiteSpace(databasePath) ? null : Path.GetFullPath(databasePath);
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
            var ledger = new TokenUsageLedger(
                databasePath ?? Path.Combine(codexHome, ".codex-quota-tray-token-usage.sqlite3"));
            await using var connection = await ledger.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var sessionStates = new Dictionary<string, SessionLedgerState?>(StringComparer.Ordinal);
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

                var existing = await TokenUsageLedger.LoadFileAsync(
                    connection, transaction, file, cancellationToken).ConfigureAwait(false);
                var pendingForkReplay = existing is { ForkReplayActive: true };
                if (existing is not null && IsUnchanged(existing, info) && !pendingForkReplay)
                {
                    continue;
                }

                RolloutInspection? inspection = null;
                if (existing is null || pendingForkReplay)
                {
                    inspection = await InspectRolloutAsync(file, cancellationToken).ConfigureAwait(false);
                    if (inspection.ForkedFromId is not null && inspection.ReplayOffset == 0)
                    {
                        await TokenUsageLedger.SaveFileAsync(
                            connection,
                            transaction,
                            new FileLedgerState(
                                file,
                                inspection.OwnerSessionId,
                                0,
                                info.Length,
                                info.LastWriteTimeUtc.Ticks,
                                info.CreationTimeUtc.Ticks,
                                null,
                                inspection.ForkedFromId,
                                ForkReplayActive: true,
                                ForkSawToken: false),
                            cancellationToken).ConfigureAwait(false);
                        filesRead++;
                        continue;
                    }
                }

                var canAppend = existing is not null && CanContinueFromCheckpoint(existing, info);
                var startOffset = inspection is { ReplayOffset: > 0 }
                    ? inspection.ReplayOffset
                    : canAppend ? existing!.Offset : 0;
                var context = new IngestContext(
                    connection,
                    transaction,
                    sessionStates,
                    sourceTimeZone,
                    file,
                    existing,
                    inspection,
                    inheritedSessionHighWater: !canAppend || inspection is { ReplayOffset: > 0 });
                var scan = await ScanSegmentAsync(
                    file,
                    startOffset,
                    context,
                    cancellationToken).ConfigureAwait(false);
                var afterScan = new FileInfo(file);
                afterScan.Refresh();
                var stableLastWrite = afterScan.Exists && afterScan.Length == scan.ObservedLength
                    ? afterScan.LastWriteTimeUtc.Ticks
                    : DateTime.MinValue.Ticks;
                await TokenUsageLedger.SaveFileAsync(
                    connection,
                    transaction,
                    new FileLedgerState(
                        file,
                        context.OwnerSessionId,
                        scan.ProcessedLength,
                        scan.ObservedLength,
                        stableLastWrite,
                        info.CreationTimeUtc.Ticks,
                        context.FileCumulative,
                        context.ForkedFromId,
                        ForkReplayActive: false,
                        ForkSawToken: false),
                    cancellationToken).ConfigureAwait(false);
                bytesRead += scan.BytesRead;
                filesRead++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            LastBytesRead = bytesRead;
            LastFilesRead = filesRead;
            TotalBytesRead += bytesRead;

            var allDays = await TokenUsageLedger.QueryDaysAsync(connection, cancellationToken).ConfigureAwait(false);
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
                yield return Path.GetFullPath(file);
            }
        }

        var archived = Path.Combine(codexHome, "archived_sessions");
        if (Directory.Exists(archived))
        {
            foreach (var file in Directory.EnumerateFiles(archived, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return Path.GetFullPath(file);
            }
        }
    }

    private static bool IsPartitionedSessionPath(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return parts.Length == 4
            && parts[0].Length == 4 && parts[0].All(char.IsDigit)
            && parts[1].Length == 2 && parts[1].All(char.IsDigit)
            && parts[2].Length == 2 && parts[2].All(char.IsDigit);
    }

    private static bool IsUnchanged(FileLedgerState state, FileInfo info) =>
        state.Length == info.Length
        && state.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks
        && state.CreationTimeUtcTicks == info.CreationTimeUtc.Ticks;

    private static bool CanContinueFromCheckpoint(FileLedgerState state, FileInfo info) =>
        state.CreationTimeUtcTicks == info.CreationTimeUtc.Ticks
        && info.Length > state.Length
        && info.Length >= state.Offset;

    private async Task<FileScanResult> ScanSegmentAsync(
        string path,
        long startOffset,
        IngestContext context,
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
            var buffered = 0;
            var markerMatches = new int[RelevantMarkers.Length];
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
                        await ProcessLineAsync(
                            lineBuffer, buffered, containsMarker, overflow, lineStartOffset == 0, context, cancellationToken)
                            .ConfigureAwait(false);
                        processedLength = absoluteOffset;
                        lineStartOffset = absoluteOffset;
                        buffered = 0;
                        Array.Clear(markerMatches);
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
                        for (var marker = 0; marker < RelevantMarkers.Length; marker++)
                        {
                            markerMatches[marker] = AdvanceMarker(RelevantMarkers[marker], markerMatches[marker], value);
                            containsMarker |= markerMatches[marker] == RelevantMarkers[marker].Length;
                        }
                    }
                }
            }

            if (lineHasContent)
            {
                var parsed = await ProcessLineAsync(
                    lineBuffer, buffered, containsMarker, overflow, lineStartOffset == 0, context, cancellationToken)
                    .ConfigureAwait(false);
                if (parsed)
                {
                    processedLength = absoluteOffset;
                }
            }

            return new FileScanResult(absoluteOffset, processedLength, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lineBuffer);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static int AdvanceMarker(byte[] marker, int matched, byte value)
    {
        if (value == marker[matched])
        {
            return matched + 1;
        }

        return value == marker[0] ? 1 : 0;
    }

    private async Task<RolloutInspection> InspectRolloutAsync(
        string path,
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
            var builder = new RolloutInspectionBuilder();
            var buffered = 0;
            var markerMatches = new int[RelevantMarkers.Length];
            var containsMarker = false;
            var overflow = false;
            var lineHasContent = false;
            var absoluteOffset = 0L;
            var lineStartOffset = 0L;
            int count;
            while ((count = await stream.ReadAsync(readBuffer.AsMemory(0, ReadBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = readBuffer[index];
                    absoluteOffset++;
                    if (value == (byte)'\n')
                    {
                        if (InspectLine(
                                lineBuffer,
                                buffered,
                                containsMarker,
                                overflow,
                                lineStartOffset == 0,
                                lineStartOffset,
                                absoluteOffset,
                                builder))
                        {
                            return builder.FinalizeInspection();
                        }

                        lineStartOffset = absoluteOffset;
                        buffered = 0;
                        Array.Clear(markerMatches);
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
                        for (var marker = 0; marker < RelevantMarkers.Length; marker++)
                        {
                            markerMatches[marker] = AdvanceMarker(RelevantMarkers[marker], markerMatches[marker], value);
                            containsMarker |= markerMatches[marker] == RelevantMarkers[marker].Length;
                        }
                    }
                }
            }

            if (lineHasContent)
            {
                _ = InspectLine(
                    lineBuffer,
                    buffered,
                    containsMarker,
                    overflow,
                    lineStartOffset == 0,
                    lineStartOffset,
                    absoluteOffset,
                    builder);
            }

            return builder.FinalizeInspection();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lineBuffer);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static bool InspectLine(
        byte[] buffer,
        int length,
        bool containsMarker,
        bool overflow,
        bool firstLine,
        long recordStart,
        long recordEnd,
        RolloutInspectionBuilder builder)
    {
        if (!containsMarker || overflow)
        {
            return false;
        }

        var offset = firstLine && length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        if (length > offset && buffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, offset, length - offset));
            var root = document.RootElement;
            var type = Text(root, "type");
            if (type.Equals("session_meta", StringComparison.Ordinal)
                && root.TryGetProperty("payload", out var metaPayload))
            {
                var candidate = Text(metaPayload, "id", "session_id");
                var forkedFromId = Text(metaPayload, "forked_from_id");
                if (string.IsNullOrEmpty(builder.OwnerSessionId))
                {
                    builder.OwnerSessionId = candidate;
                    builder.ForkedFromId = string.IsNullOrEmpty(forkedFromId) ? null : forkedFromId;
                    return builder.ForkedFromId is null;
                }

                if (!string.IsNullOrEmpty(candidate)
                    && !candidate.Equals(builder.OwnerSessionId, StringComparison.Ordinal)
                    && (builder.ForkedFromId is null
                        || candidate.Equals(builder.ForkedFromId, StringComparison.Ordinal)))
                {
                    if (builder.Baseline is null || builder.Baseline.Value.Total == 0)
                    {
                        builder.ModernPrefix = true;
                    }
                    else if (builder.LegacyReplayOffset == 0)
                    {
                        builder.LegacyReplayOffset = recordEnd;
                        builder.LegacyBaseline = builder.Baseline;
                    }
                }

                return false;
            }

            if (!type.Equals("event_msg", StringComparison.Ordinal)
                || builder.ForkedFromId is null
                || !root.TryGetProperty("payload", out var payload))
            {
                return false;
            }

            var payloadType = Text(payload, "type");
            if (payloadType.Equals("task_started", StringComparison.Ordinal)
                && UuidV7AtOrAfter(Text(payload, "turn_id"), builder.OwnerSessionId))
            {
                builder.ReplayOffset = recordEnd;
                return true;
            }

            if (!payloadType.Equals("token_count", StringComparison.Ordinal)
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var current = WithMissingSubsets(Counters(info, "total_token_usage"), builder.Baseline);
            if (current is null)
            {
                return false;
            }

            if (!builder.HasImplicitCandidate
                && TryImplicitForkBaseline(current.Value, Counters(info, "last_token_usage"), out var implicitBaseline))
            {
                builder.HasImplicitCandidate = true;
                builder.ImplicitReplayOffset = recordStart;
                builder.ImplicitBaseline = implicitBaseline;
            }

            builder.Baseline = current;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TokenCounters? WithMissingSubsets(TokenCounters? current, TokenCounters? previous) =>
        current is null
            ? null
            : new TokenCounters(
                current.Value.Total,
                current.Value.Input ?? previous?.Input,
                current.Value.Cached ?? previous?.Cached,
                current.Value.Output ?? previous?.Output,
                current.Value.Reasoning ?? previous?.Reasoning);

    private static bool TryImplicitForkBaseline(
        TokenCounters total,
        TokenCounters? last,
        out TokenCounters baseline)
    {
        baseline = default;
        if (last is null || !ValidTokenUsage(total) || !ValidTokenUsage(last.Value) || !MonotonicFrom(total, last.Value))
        {
            return false;
        }

        baseline = new TokenCounters(
            total.Total - last.Value.Total,
            total.Input!.Value - last.Value.Input!.Value,
            total.Cached!.Value - last.Value.Cached!.Value,
            total.Output!.Value - last.Value.Output!.Value,
            total.Reasoning!.Value - last.Value.Reasoning!.Value);
        return ValidTokenUsage(baseline);
    }

    private static bool ValidTokenUsage(TokenCounters value) =>
        value.Total >= 0
        && value.Input is >= 0
        && value.Cached is >= 0
        && value.Output is >= 0
        && value.Reasoning is >= 0
        && value.Cached <= value.Input
        && value.Reasoning <= value.Output
        && value.Input + value.Output == value.Total;

    private static bool MonotonicFrom(TokenCounters current, TokenCounters previous) =>
        current.Total >= previous.Total
        && current.Input >= previous.Input
        && current.Cached >= previous.Cached
        && current.Output >= previous.Output
        && current.Reasoning >= previous.Reasoning;

    private static bool UuidV7AtOrAfter(string candidate, string owner)
    {
        if (candidate.Length != 36 || owner.Length != 36 || candidate[14] != '7' || owner[14] != '7')
        {
            return false;
        }

        foreach (var index in new[] { 8, 13, 18, 23 })
        {
            if (candidate[index] != '-' || owner[index] != '-')
            {
                return false;
            }
        }

        for (var index = 0; index < 36; index++)
        {
            if (index is 8 or 13 or 18 or 23)
            {
                continue;
            }

            if (!Uri.IsHexDigit(candidate[index]) || !Uri.IsHexDigit(owner[index]))
            {
                return false;
            }
        }

        return string.Compare(candidate, owner, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static async Task<bool> ProcessLineAsync(
        byte[] buffer,
        int length,
        bool containsMarker,
        bool overflow,
        bool firstLine,
        IngestContext context,
        CancellationToken cancellationToken)
    {
        if (!containsMarker || overflow)
        {
            return false;
        }

        var offset = firstLine && length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        if (length > offset && buffer[length - 1] == (byte)'\r')
        {
            length--;
        }

        try
        {
            using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, offset, length - offset));
            var root = document.RootElement;
            var type = Text(root, "type");
            if (type.Equals("session_meta", StringComparison.Ordinal)
                && root.TryGetProperty("payload", out var metaPayload))
            {
                await context.ApplySessionMetaAsync(
                    Text(metaPayload, "id", "session_id"),
                    Text(metaPayload, "forked_from_id"),
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!type.Equals("event_msg", StringComparison.Ordinal)
                || !root.TryGetProperty("payload", out var payload))
            {
                return false;
            }

            var payloadType = Text(payload, "type");
            if (payloadType.Equals("task_started", StringComparison.Ordinal))
            {
                return true;
            }

            if (!payloadType.Equals("token_count", StringComparison.Ordinal)
                || !root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var total = Counters(info, "total_token_usage");
            if (total is null)
            {
                return false;
            }

            await context.ApplyTokenAsync(
                timestamp.ToUniversalTime(), total.Value, Counters(info, "last_token_usage"), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (JsonException error)
        {
            Debug.WriteLine($"TokenUsage scanner skipped malformed JSON: {error.GetType().Name}");
            return false;
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
        return total is null
            ? null
            : new TokenCounters(
                Math.Max(0, total.Value),
                NonNegative(input),
                NonNegative(Long(value, "cached_input_tokens") ?? Long(value, "cache_read_input_tokens")),
                NonNegative(output),
                NonNegative(Long(value, "reasoning_output_tokens")));
    }

    private static TokenCounters Delta(TokenCounters previous, TokenCounters current) => new(
        Math.Max(0, current.Total - previous.Total),
        Difference(previous.Input, current.Input, false),
        Difference(previous.Cached, current.Cached, false),
        Difference(previous.Output, current.Output, false),
        Difference(previous.Reasoning, current.Reasoning, false));

    private static TokenCounters Correction(TokenCounters previous, TokenCounters current) => new(
        0,
        Difference(previous.Input, current.Input, true),
        Difference(previous.Cached, current.Cached, true),
        Difference(previous.Output, current.Output, true),
        Difference(previous.Reasoning, current.Reasoning, true));

    private static long? Difference(long? previous, long? current, bool allowNegative) =>
        previous is not null && current is not null
            ? allowNegative ? current.Value - previous.Value : Math.Max(0, current.Value - previous.Value)
            : null;

    private static string EventKey(string sessionId, long segment, TokenCounters current)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"jsonl\0{sessionId}\0{segment}\0{current.Input}\0{current.Cached}\0{current.Output}\0{current.Reasoning}\0{current.Total}");
        return "jsonl:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string Text(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

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

    private sealed class IngestContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Dictionary<string, SessionLedgerState?> sessionStates,
        TimeZoneInfo sourceTimeZone,
        string path,
        FileLedgerState? existing,
        RolloutInspection? inspection,
        bool inheritedSessionHighWater)
    {
        private SessionLedgerState? session;
        private bool sessionLoaded;

        private readonly TokenCounters? initialBaseline = inspection is { ReplayOffset: > 0 }
            ? inspection.Baseline
            : existing?.ForkedFromId is not null ? existing.Cumulative : null;

        internal string OwnerSessionId { get; private set; } = !string.IsNullOrEmpty(inspection?.OwnerSessionId)
            ? inspection.OwnerSessionId
            : existing?.OwnerSessionId ?? string.Empty;

        internal TokenCounters? FileCumulative { get; private set; } = inspection is { ReplayOffset: > 0 }
            ? inspection.Baseline
            : existing?.Cumulative;

        internal string? ForkedFromId { get; private set; } = inspection?.ForkedFromId ?? existing?.ForkedFromId;

        internal async Task ApplySessionMetaAsync(string sessionId, string forkedFromId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(OwnerSessionId) && !string.IsNullOrEmpty(sessionId))
            {
                OwnerSessionId = sessionId;
                ForkedFromId = string.IsNullOrEmpty(forkedFromId) ? null : forkedFromId;
                await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

        }

        internal async Task ApplyTokenAsync(
            DateTimeOffset timestamp,
            TokenCounters current,
            TokenCounters? last,
            CancellationToken cancellationToken)
        {
            await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
            FileCumulative = current;
            if (session is null)
            {
                await InsertDeltaAsync(timestamp, current, current, cancellationToken).ConfigureAwait(false);
                return;
            }

            var previous = session.Cumulative;
            if (inheritedSessionHighWater && current.Total <= previous.Total)
            {
                if (current.Total == previous.Total)
                {
                    inheritedSessionHighWater = false;
                }

                return;
            }

            if (current.Total == previous.Total)
            {
                if (current.Equals(previous))
                {
                    return;
                }

                await TokenUsageLedger.CorrectEventAsync(
                    connection,
                    transaction,
                    session.LastEventId ?? string.Empty,
                    Correction(previous, current),
                    cancellationToken).ConfigureAwait(false);
                session = session with { Cumulative = current };
                await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (current.Total > previous.Total)
            {
                inheritedSessionHighWater = false;
                await InsertDeltaAsync(timestamp, Delta(previous, current), current, cancellationToken).ConfigureAwait(false);
                return;
            }

            var resetDelta = last is not null && last.Value.Total <= current.Total ? last.Value : current;
            session = session with { Segment = session.Segment + 1 };
            await InsertDeltaAsync(timestamp, resetDelta, current, cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureOwnerAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(OwnerSessionId))
            {
                OwnerSessionId = Path.GetFileNameWithoutExtension(path);
            }

            await LoadSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task LoadSessionAsync(CancellationToken cancellationToken)
        {
            if (sessionLoaded)
            {
                return;
            }

            if (!sessionStates.TryGetValue(OwnerSessionId, out session))
            {
                session = await TokenUsageLedger.LoadSessionAsync(
                    connection, transaction, OwnerSessionId, cancellationToken).ConfigureAwait(false);
                if (session is null && initialBaseline is not null)
                {
                    session = new SessionLedgerState(
                        OwnerSessionId,
                        initialBaseline.Value,
                        ForkedFromId,
                        LastEventId: null,
                        Segment: 0);
                }
                sessionStates.Add(OwnerSessionId, session);
            }

            sessionLoaded = true;
        }

        private async Task InsertDeltaAsync(
            DateTimeOffset timestamp,
            TokenCounters delta,
            TokenCounters cumulative,
            CancellationToken cancellationToken)
        {
            var segment = session?.Segment ?? 0;
            var eventId = EventKey(OwnerSessionId, segment, cumulative);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, sourceTimeZone).DateTime);
            var inserted = await TokenUsageLedger.InsertEventAsync(
                connection,
                transaction,
                new LedgerTokenEvent(eventId, OwnerSessionId, timestamp, localDate, delta),
                cancellationToken).ConfigureAwait(false);
            session = new SessionLedgerState(
                OwnerSessionId,
                cumulative,
                ForkedFromId ?? session?.ForkedFromId,
                inserted ? eventId : session?.LastEventId,
                segment);
            await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveSessionAsync(CancellationToken cancellationToken)
        {
            if (session is null)
            {
                return;
            }

            await TokenUsageLedger.SaveSessionAsync(
                connection, transaction, session, cancellationToken).ConfigureAwait(false);
            sessionStates[OwnerSessionId] = session;
        }
    }

    private sealed class RolloutInspectionBuilder
    {
        internal string OwnerSessionId { get; set; } = string.Empty;

        internal string? ForkedFromId { get; set; }

        internal long ReplayOffset { get; set; }

        internal TokenCounters? Baseline { get; set; }

        internal bool HasImplicitCandidate { get; set; }

        internal long ImplicitReplayOffset { get; set; }

        internal TokenCounters ImplicitBaseline { get; set; }

        internal long LegacyReplayOffset { get; set; }

        internal TokenCounters? LegacyBaseline { get; set; }

        internal bool ModernPrefix { get; set; }

        internal RolloutInspection FinalizeInspection()
        {
            if (ReplayOffset == 0 && LegacyReplayOffset > 0)
            {
                ReplayOffset = LegacyReplayOffset;
                Baseline = LegacyBaseline;
            }
            else if (ReplayOffset == 0 && ModernPrefix)
            {
                Baseline = null;
            }
            else if (ReplayOffset == 0 && ForkedFromId is not null && HasImplicitCandidate)
            {
                ReplayOffset = ImplicitReplayOffset;
                Baseline = ImplicitBaseline;
            }

            return new RolloutInspection(OwnerSessionId, ForkedFromId, ReplayOffset, Baseline);
        }
    }

    private sealed record RolloutInspection(
        string OwnerSessionId,
        string? ForkedFromId,
        long ReplayOffset,
        TokenCounters? Baseline);

    private sealed record FileScanResult(long ObservedLength, long ProcessedLength, long BytesRead);
}
