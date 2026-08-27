using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodexQuotaTray.Core.TokenUsage;

internal sealed class TokenUsageLedger(string databasePath)
{
    private const int SchemaVersion = 2;

    internal string DatabasePath { get; } = Path.GetFullPath(databasePath);

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("A Token usage database directory is required.");
        Directory.CreateDirectory(directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal static async Task<FileLedgerState?> LoadFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT owner_session_id, offset, length, last_write_time_utc, creation_time_utc,
                   total_tokens, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens,
                   forked_from_id, fork_replay_active, fork_saw_token
            FROM file_state
            WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new FileLedgerState(
            path,
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            ReadCounters(reader, 5),
            NullString(reader, 10),
            reader.GetInt64(11) != 0,
            reader.GetInt64(12) != 0);
    }

    internal static async Task SaveFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FileLedgerState state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_state(
                path, owner_session_id, offset, length, last_write_time_utc, creation_time_utc,
                total_tokens, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens,
                forked_from_id, fork_replay_active, fork_saw_token)
            VALUES(
                $path, $owner, $offset, $length, $write, $creation,
                $total, $input, $cached, $output, $reasoning,
                $forkedFrom, $replayActive, $sawToken)
            ON CONFLICT(path) DO UPDATE SET
                owner_session_id = excluded.owner_session_id,
                offset = excluded.offset,
                length = excluded.length,
                last_write_time_utc = excluded.last_write_time_utc,
                creation_time_utc = excluded.creation_time_utc,
                total_tokens = excluded.total_tokens,
                input_tokens = excluded.input_tokens,
                cached_input_tokens = excluded.cached_input_tokens,
                output_tokens = excluded.output_tokens,
                reasoning_tokens = excluded.reasoning_tokens,
                forked_from_id = excluded.forked_from_id,
                fork_replay_active = excluded.fork_replay_active,
                fork_saw_token = excluded.fork_saw_token;
            """;
        Add(command, "$path", state.Path);
        Add(command, "$owner", state.OwnerSessionId);
        Add(command, "$offset", state.Offset);
        Add(command, "$length", state.Length);
        Add(command, "$write", state.LastWriteTimeUtcTicks);
        Add(command, "$creation", state.CreationTimeUtcTicks);
        AddCounters(command, state.Cumulative);
        Add(command, "$forkedFrom", state.ForkedFromId);
        Add(command, "$replayActive", state.ForkReplayActive ? 1 : 0);
        Add(command, "$sawToken", state.ForkSawToken ? 1 : 0);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SessionLedgerState?> LoadSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT total_tokens, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens,
                   forked_from_id, last_event_id, segment
            FROM session_state
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SessionLedgerState(
            sessionId,
            ReadCounters(reader, 0)!.Value,
            NullString(reader, 5),
            NullString(reader, 6),
            reader.GetInt64(7));
    }

    internal static async Task SaveSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionLedgerState state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_state(
                session_id, total_tokens, input_tokens, cached_input_tokens, output_tokens,
                reasoning_tokens, forked_from_id, last_event_id, segment)
            VALUES(
                $sessionId, $total, $input, $cached, $output,
                $reasoning, $forkedFrom, $lastEventId, $segment)
            ON CONFLICT(session_id) DO UPDATE SET
                total_tokens = excluded.total_tokens,
                input_tokens = excluded.input_tokens,
                cached_input_tokens = excluded.cached_input_tokens,
                output_tokens = excluded.output_tokens,
                reasoning_tokens = excluded.reasoning_tokens,
                forked_from_id = COALESCE(session_state.forked_from_id, excluded.forked_from_id),
                last_event_id = excluded.last_event_id,
                segment = excluded.segment;
            """;
        Add(command, "$sessionId", state.SessionId);
        AddCounters(command, state.Cumulative);
        Add(command, "$forkedFrom", state.ForkedFromId);
        Add(command, "$lastEventId", state.LastEventId);
        Add(command, "$segment", state.Segment);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LedgerTokenEvent value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO token_events(
                event_id, session_id, timestamp, local_date, total_tokens,
                input_tokens, cached_input_tokens, output_tokens, reasoning_tokens)
            VALUES(
                $eventId, $sessionId, $timestamp, $localDate, $total,
                $input, $cached, $output, $reasoning);
            """;
        Add(command, "$eventId", value.EventId);
        Add(command, "$sessionId", value.SessionId);
        Add(command, "$timestamp", value.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$localDate", value.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddCounters(command, value.Delta);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (inserted)
        {
            await UpdateDailyAggregateAsync(
                connection,
                transaction,
                value.LocalDate,
                value.Delta,
                cancellationToken).ConfigureAwait(false);
        }

        return inserted;
    }

    internal static async Task CorrectEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        TokenCounters difference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(eventId) || difference.Total != 0)
        {
            return;
        }

        string? localDate;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT local_date FROM token_events WHERE event_id = $eventId;";
            Add(lookup, "$eventId", eventId);
            localDate = Convert.ToString(
                await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrEmpty(localDate)
            || !DateOnly.TryParseExact(
                localDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE token_events
            SET input_tokens = CASE WHEN input_tokens IS NULL OR $input IS NULL THEN NULL ELSE input_tokens + $input END,
                cached_input_tokens = CASE WHEN cached_input_tokens IS NULL OR $cached IS NULL THEN NULL ELSE cached_input_tokens + $cached END,
                output_tokens = CASE WHEN output_tokens IS NULL OR $output IS NULL THEN NULL ELSE output_tokens + $output END,
                reasoning_tokens = CASE WHEN reasoning_tokens IS NULL OR $reasoning IS NULL THEN NULL ELSE reasoning_tokens + $reasoning END
            WHERE event_id = $eventId;
            """;
        Add(command, "$eventId", eventId);
        Add(command, "$input", difference.Input);
        Add(command, "$cached", difference.Cached);
        Add(command, "$output", difference.Output);
        Add(command, "$reasoning", difference.Reasoning);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            await UpdateDailyAggregateAsync(
                connection,
                transaction,
                date,
                difference,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<IReadOnlyList<TokenUsageDay>> QueryDaysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_date, total_tokens, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens
            FROM token_daily_aggregate
            ORDER BY local_date;
            """;
        var result = new List<TokenUsageDay>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!DateOnly.TryParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                continue;
            }

            result.Add(new TokenUsageDay(
                date,
                reader.GetInt64(1),
                NullableInt64(reader, 2),
                NullableInt64(reader, 3),
                NullableInt64(reader, 4),
                NullableInt64(reader, 5)));
        }

        return result;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ledger_meta(
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_state(
                session_id TEXT PRIMARY KEY,
                total_tokens INTEGER NOT NULL,
                input_tokens INTEGER,
                cached_input_tokens INTEGER,
                output_tokens INTEGER,
                reasoning_tokens INTEGER,
                forked_from_id TEXT,
                last_event_id TEXT,
                segment INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS file_state(
                path TEXT PRIMARY KEY,
                owner_session_id TEXT NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                last_write_time_utc INTEGER NOT NULL,
                creation_time_utc INTEGER NOT NULL,
                total_tokens INTEGER,
                input_tokens INTEGER,
                cached_input_tokens INTEGER,
                output_tokens INTEGER,
                reasoning_tokens INTEGER,
                forked_from_id TEXT,
                fork_replay_active INTEGER NOT NULL DEFAULT 0,
                fork_saw_token INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS token_events(
                event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                local_date TEXT NOT NULL,
                total_tokens INTEGER NOT NULL,
                input_tokens INTEGER,
                cached_input_tokens INTEGER,
                output_tokens INTEGER,
                reasoning_tokens INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_token_events_local_date ON token_events(local_date);
            CREATE TABLE IF NOT EXISTS token_daily_aggregate(
                local_date TEXT PRIMARY KEY,
                total_tokens INTEGER NOT NULL,
                input_tokens INTEGER,
                cached_input_tokens INTEGER,
                output_tokens INTEGER,
                reasoning_tokens INTEGER
            );
            INSERT INTO ledger_meta(key, value) VALUES('schema_version', $schemaVersion)
            ON CONFLICT(key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$schemaVersion", "1");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var version = connection.CreateCommand();
        version.Transaction = (SqliteTransaction)transaction;
        version.CommandText = "SELECT value FROM ledger_meta WHERE key = 'schema_version';";
        var stored = Convert.ToString(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported Token usage ledger schema version '{stored}'.");
        }

        if (parsed == 1)
        {
            await using var migrate = connection.CreateCommand();
            migrate.Transaction = (SqliteTransaction)transaction;
            migrate.CommandText = """
                DELETE FROM token_daily_aggregate;
                INSERT INTO token_daily_aggregate(
                    local_date, total_tokens, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens)
                SELECT local_date,
                       SUM(total_tokens),
                       CASE WHEN COUNT(*) = COUNT(input_tokens) THEN SUM(input_tokens) END,
                       CASE WHEN COUNT(*) = COUNT(cached_input_tokens) THEN SUM(cached_input_tokens) END,
                       CASE WHEN COUNT(*) = COUNT(output_tokens) THEN SUM(output_tokens) END,
                       CASE WHEN COUNT(*) = COUNT(reasoning_tokens) THEN SUM(reasoning_tokens) END
                FROM token_events
                GROUP BY local_date;
                UPDATE ledger_meta SET value = $schemaVersion WHERE key = 'schema_version';
                """;
            migrate.Parameters.AddWithValue("$schemaVersion", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            _ = await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateDailyAggregateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly localDate,
        TokenCounters delta,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO token_daily_aggregate(
                local_date, total_tokens, input_tokens, cached_input_tokens, output_tokens, reasoning_tokens)
            VALUES($localDate, $total, $input, $cached, $output, $reasoning)
            ON CONFLICT(local_date) DO UPDATE SET
                total_tokens = token_daily_aggregate.total_tokens + excluded.total_tokens,
                input_tokens = CASE
                    WHEN token_daily_aggregate.input_tokens IS NULL OR excluded.input_tokens IS NULL THEN NULL
                    ELSE token_daily_aggregate.input_tokens + excluded.input_tokens
                END,
                cached_input_tokens = CASE
                    WHEN token_daily_aggregate.cached_input_tokens IS NULL OR excluded.cached_input_tokens IS NULL THEN NULL
                    ELSE token_daily_aggregate.cached_input_tokens + excluded.cached_input_tokens
                END,
                output_tokens = CASE
                    WHEN token_daily_aggregate.output_tokens IS NULL OR excluded.output_tokens IS NULL THEN NULL
                    ELSE token_daily_aggregate.output_tokens + excluded.output_tokens
                END,
                reasoning_tokens = CASE
                    WHEN token_daily_aggregate.reasoning_tokens IS NULL OR excluded.reasoning_tokens IS NULL THEN NULL
                    ELSE token_daily_aggregate.reasoning_tokens + excluded.reasoning_tokens
                END;
            """;
        Add(command, "$localDate", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddCounters(command, delta);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddCounters(SqliteCommand command, TokenCounters? value)
    {
        Add(command, "$total", value?.Total);
        Add(command, "$input", value?.Input);
        Add(command, "$cached", value?.Cached);
        Add(command, "$output", value?.Output);
        Add(command, "$reasoning", value?.Reasoning);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static TokenCounters? ReadCounters(SqliteDataReader reader, int start)
    {
        if (reader.IsDBNull(start))
        {
            return null;
        }

        return new TokenCounters(
            reader.GetInt64(start),
            NullableInt64(reader, start + 1),
            NullableInt64(reader, start + 2),
            NullableInt64(reader, start + 3),
            NullableInt64(reader, start + 4));
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

internal readonly record struct TokenCounters(
    long Total,
    long? Input,
    long? Cached,
    long? Output,
    long? Reasoning);

internal sealed record FileLedgerState(
    string Path,
    string OwnerSessionId,
    long Offset,
    long Length,
    long LastWriteTimeUtcTicks,
    long CreationTimeUtcTicks,
    TokenCounters? Cumulative,
    string? ForkedFromId,
    bool ForkReplayActive,
    bool ForkSawToken);

internal sealed record SessionLedgerState(
    string SessionId,
    TokenCounters Cumulative,
    string? ForkedFromId,
    string? LastEventId,
    long Segment);

internal sealed record LedgerTokenEvent(
    string EventId,
    string SessionId,
    DateTimeOffset TimestampUtc,
    DateOnly LocalDate,
    TokenCounters Delta);
