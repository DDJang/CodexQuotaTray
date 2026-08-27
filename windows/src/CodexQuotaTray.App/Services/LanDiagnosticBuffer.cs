using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodexQuotaTray.Core;

namespace CodexQuotaTray.App.Services;

internal sealed record LanDiagnosticState(
    bool Paired = false,
    string? DeviceId = null,
    string? Endpoint = null,
    string ListenerStatus = "unavailable",
    string? BindAddress = null,
    int? Port = null,
    uint? InterfaceIndex = null,
    string DnsSdStatus = "unavailable",
    uint? DnsSdInterfaceIndex = null,
    DateTimeOffset? LastSuccessUtc = null,
    DateTimeOffset? LastFailureUtc = null,
    string? LastFailurePhase = null,
    long? LastAttemptId = null,
    string? LastAttemptChannel = null,
    string? LastRemoteAddress = null,
    DateTimeOffset? LastRequestUtc = null,
    string? LastRequestResult = null,
    string? LastSuccessfulRemoteAddress = null,
    DateTimeOffset? LastNetworkChangeUtc = null,
    string? LastNetworkChangeReason = null,
    DateTimeOffset? LastReconcileUtc = null,
    string? LastReconcileReason = null,
    string? LastReconcileResult = null,
    DateTimeOffset? LastRepairProbeUtc = null,
    string? LastRepairProbeResult = null,
    string? LastRepairActionResult = null,
    string? LastRepairRemote = null,
    string? LastRepairProbeKind = null,
    string? LastRepairLocalAddress = null,
    uint? LastRepairInterfaceIndex = null,
    bool? LastRepairSourceBound = null,
    string? LastRepairFailureKind = null,
    string? LastRepairNeighborBeforeState = null,
    string? LastRepairNeighborBeforeMac = null,
    string? LastRepairNeighborBeforeError = null,
    string? LastRepairNeighborAfterState = null,
    string? LastRepairNeighborAfterMac = null,
    string? LastRepairNeighborAfterError = null);

internal static class LanDiagnosticRedactor
{
    private const int MaximumLineLength = 512;
    private static readonly Regex AuthorizationPattern = new(
        @"(?i)authorization\s*:\s*bearer\s+[^\s,;]+|authorization\s*=\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern = new(
        @"(?i)(pairing[_ -]?secret\s*=\s*|client[_ -]?secret\s*=\s*|access[_ -]?token\s*=\s*|refresh[_ -]?token\s*=\s*|token\s*=\s*|cookie\s*=\s*|password\s*=\s*)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PairingUriTokenPattern = new(
        @"(?i)([?&]token=)[^&\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Sanitize(string? value)
    {
        var safe = Redact(value ?? string.Empty);
        safe = safe.Replace("\r", " ").Replace("\n", " ");
        return safe.Length <= MaximumLineLength ? safe : safe[..MaximumLineLength];
    }

    internal static string SanitizeText(string? value)
    {
        var safe = Redact(value ?? string.Empty);
        return safe.Length <= 32_000 ? safe : safe[..32_000];
    }

    private static string Redact(string value)
    {
        var safe = AuthorizationPattern.Replace(value, "[redacted]");
        safe = PairingUriTokenPattern.Replace(safe, "$1[redacted]");
        return SecretPattern.Replace(safe, "$1[redacted]");
    }
}

internal sealed class LanDiagnosticBuffer : IAsyncDisposable
{
    internal const int MaximumEntries = 200;
    internal const int MaximumSlotBytes = 1024 * 1024;
    internal const int SlotCount = 3;

    private readonly object gate = new();
    private readonly Queue<string> entries = new();
    private readonly Channel<string> pending = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource lifetime = new();
    private readonly string? persistenceDirectory;
    private readonly string? statePath;
    private readonly Task persistenceTask;
    private LanDiagnosticState state;
    private bool stateDirty;
    private DateTimeOffset lastStateWriteUtc;
    private int disposed;

    internal LanDiagnosticBuffer(string? dataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            persistenceDirectory = Path.Combine(dataDirectory, "lan-diagnostics");
            statePath = Path.Combine(persistenceDirectory, "state.json");
        }

        state = LoadState();
        LoadEntries();
        persistenceTask = PersistAsync();
    }

    internal LanDiagnosticState Snapshot
    {
        get { lock (gate) return state; }
    }

    internal void Record(string message)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var safe = LanDiagnosticRedactor.Sanitize(message);
        var line = $"{timestamp:O} {safe}";
        lock (gate)
        {
            entries.Enqueue(line);
            while (entries.Count > MaximumEntries) entries.Dequeue();
            state = UpdateState(state, safe, timestamp);
            stateDirty = true;
        }

        try
        {
            if (Volatile.Read(ref disposed) == 0) pending.Writer.TryWrite(line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Diagnostics are fail-open by design.
        }

        System.Diagnostics.Debug.WriteLine(safe);
    }

    internal string CreateDiagnosticText()
    {
        string[] recent;
        LanDiagnosticState current;
        lock (gate)
        {
            recent = entries.ToArray();
            current = state;
        }

        return LanDiagnosticsFormatter.FormatWindows(
            ProductVersion.Current,
            current,
            recent,
            DateTimeOffset.UtcNow);
    }

    internal string CreateRecentEventsText()
    {
        lock (gate)
        {
            return entries.Count == 0 ? "LAN 诊断: 暂无记录" : string.Join(Environment.NewLine, entries);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pending.Writer.TryComplete();
        try
        {
            await persistenceTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lifetime.Cancel();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            lifetime.Cancel();
        }
        finally
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            await foreach (var line in pending.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                await AppendLineAsync(line, lifetime.Token).ConfigureAwait(false);
                await PersistStateIfDueAsync(force: false, lifetime.Token).ConfigureAwait(false);
            }

            await PersistStateIfDueAsync(force: true, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // A broken or unavailable data directory must not affect LAN work.
        }
    }

    private async Task AppendLineAsync(string line, CancellationToken cancellationToken)
    {
        if (persistenceDirectory is null) return;
        try
        {
            Directory.CreateDirectory(persistenceDirectory);
            var path = SlotPath(0);
            var lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            var currentBytes = File.Exists(path) ? new FileInfo(path).Length : 0L;
            if (currentBytes + lineBytes > MaximumSlotBytes)
            {
                RotateSlots();
            }

            await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Persistence is best-effort and fail-open.
        }
    }

    private async Task PersistStateIfDueAsync(bool force, CancellationToken cancellationToken)
    {
        if (statePath is null) return;
        LanDiagnosticState current;
        lock (gate)
        {
            if (!stateDirty || !force && DateTimeOffset.UtcNow - lastStateWriteUtc < TimeSpan.FromMilliseconds(500)) return;
            current = state;
            stateDirty = false;
            lastStateWriteUtc = DateTimeOffset.UtcNow;
        }

        try
        {
            Directory.CreateDirectory(persistenceDirectory!);
            var temporary = statePath + ".tmp";
            var json = JsonSerializer.Serialize(current);
            await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, statePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            lock (gate) stateDirty = true;
        }
    }

    private void RotateSlots()
    {
        try
        {
            for (var index = SlotCount - 1; index > 0; index--)
            {
                var source = SlotPath(index - 1);
                var destination = SlotPath(index);
                if (File.Exists(source)) File.Move(source, destination, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private void LoadEntries()
    {
        if (persistenceDirectory is null) return;
        try
        {
            for (var index = SlotCount - 1; index >= 0; index--)
            {
                var path = SlotPath(index);
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path).TakeLast(MaximumEntries))
                {
                    var safe = LanDiagnosticRedactor.Sanitize(line);
                    entries.Enqueue(safe);
                    while (entries.Count > MaximumEntries) entries.Dequeue();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private LanDiagnosticState LoadState()
    {
        if (statePath is null) return new LanDiagnosticState();
        try
        {
            return JsonSerializer.Deserialize<LanDiagnosticState>(File.ReadAllText(statePath)) ?? new LanDiagnosticState();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new LanDiagnosticState();
        }
    }

    private string SlotPath(int index) => Path.Combine(persistenceDirectory!, $"events-{index}.log");

    private static LanDiagnosticState UpdateState(LanDiagnosticState current, string line, DateTimeOffset timestamp)
    {
        var paired = current.Paired || line.Contains("LAN pairing", StringComparison.OrdinalIgnoreCase);
        var next = current with
        {
            Paired = paired,
            DeviceId = Value(line, "device") ?? current.DeviceId,
            Endpoint = Value(line, "endpoint") ?? current.Endpoint,
            BindAddress = Value(line, "bind") ?? current.BindAddress,
            Port = ParseInt(Value(line, "port")) ?? current.Port,
            InterfaceIndex = ParseUInt(Value(line, "interfaceIndex")) ?? current.InterfaceIndex,
            DnsSdInterfaceIndex = ParseUInt(Value(line, "interface")) ?? current.DnsSdInterfaceIndex,
        };

        if (line.Contains("listener healthy=true", StringComparison.OrdinalIgnoreCase)
            || line.Contains("listener started", StringComparison.OrdinalIgnoreCase))
        {
            next = next with { ListenerStatus = "healthy" };
        }
        else if (line.Contains("listener healthy=false", StringComparison.OrdinalIgnoreCase)
            || line.Contains("listener unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            next = next with { ListenerStatus = "unhealthy" };
        }
        else if (line.Contains("listener stopped", StringComparison.OrdinalIgnoreCase))
        {
            next = next with { ListenerStatus = "stopped" };
        }

        if (line.Contains("DNS-SD registration success", StringComparison.OrdinalIgnoreCase)
            || line.Contains("DNS-SD register success", StringComparison.OrdinalIgnoreCase))
        {
            next = next with { DnsSdStatus = "success" };
        }
        else if (line.Contains("DNS-SD", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("failure", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unavailable", StringComparison.OrdinalIgnoreCase)))
        {
            next = next with { DnsSdStatus = "failure" };
        }

        var result = Value(line, "result");
        if (line.Contains("LAN request", StringComparison.OrdinalIgnoreCase) && result is not null)
        {
            next = next with { LastRequestUtc = timestamp, LastRequestResult = result };
            if (string.Equals(result, "SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                next = next with
                {
                    LastSuccessUtc = timestamp,
                    LastRemoteAddress = Value(line, "remote") ?? current.LastRemoteAddress,
                    LastSuccessfulRemoteAddress = Value(line, "remote") ?? current.LastSuccessfulRemoteAddress,
                };
            }
            else
            {
                next = next with { LastFailureUtc = timestamp, LastFailurePhase = result.ToUpperInvariant() };
            }
        }

        if (line.Contains("Network change observed", StringComparison.OrdinalIgnoreCase))
        {
            next = next with
            {
                LastNetworkChangeUtc = timestamp,
                LastNetworkChangeReason = Value(line, "reason") ?? current.LastNetworkChangeReason,
            };
        }

        if (line.Contains("LAN reconcile result=", StringComparison.OrdinalIgnoreCase))
        {
            next = next with
            {
                LastReconcileUtc = timestamp,
                LastReconcileReason = Value(line, "reason") ?? current.LastReconcileReason,
                LastReconcileResult = Value(line, "result") ?? current.LastReconcileResult,
            };
        }

        if (line.Contains("LAN repair probe completed", StringComparison.OrdinalIgnoreCase))
        {
            next = next with
            {
                LastRepairProbeUtc = timestamp,
                LastRepairProbeResult = Value(line, "probeResult") ?? current.LastRepairProbeResult,
                LastRepairActionResult = Value(line, "actionResult") ?? current.LastRepairActionResult,
                LastRepairRemote = Value(line, "remote") ?? current.LastRepairRemote,
                LastRepairFailureKind = Value(line, "failureKind") ?? current.LastRepairFailureKind,
                LastRepairLocalAddress = Value(line, "localAddress") ?? current.LastRepairLocalAddress,
                LastRepairInterfaceIndex = ParseUInt(Value(line, "interfaceIndex")) ?? current.LastRepairInterfaceIndex,
                LastRepairSourceBound = ParseBool(Value(line, "sourceBound")) ?? current.LastRepairSourceBound,
                LastRepairNeighborAfterState = Value(line, "neighborAfterState") ?? current.LastRepairNeighborAfterState,
                LastRepairNeighborAfterMac = Value(line, "neighborAfterMac") ?? current.LastRepairNeighborAfterMac,
                LastRepairNeighborAfterError = Value(line, "neighborAfterError") ?? current.LastRepairNeighborAfterError,
            };
        }

        if (line.Contains("LAN repair probe started", StringComparison.OrdinalIgnoreCase))
        {
            next = next with
            {
                LastRepairProbeKind = Value(line, "probeKind") ?? current.LastRepairProbeKind,
                LastRepairLocalAddress = Value(line, "localAddress") ?? current.LastRepairLocalAddress,
                LastRepairInterfaceIndex = ParseUInt(Value(line, "interfaceIndex")) ?? current.LastRepairInterfaceIndex,
                LastRepairSourceBound = ParseBool(Value(line, "sourceBound")) ?? current.LastRepairSourceBound,
                LastRepairNeighborBeforeState = Value(line, "neighborBeforeState") ?? current.LastRepairNeighborBeforeState,
                LastRepairNeighborBeforeMac = Value(line, "neighborBeforeMac") ?? current.LastRepairNeighborBeforeMac,
                LastRepairNeighborBeforeError = Value(line, "neighborBeforeError") ?? current.LastRepairNeighborBeforeError,
            };
        }

        return next;
    }

    private static string? Value(string line, string key)
    {
        var marker = key + "=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = line.IndexOf(' ', start);
        return (end < 0 ? line[start..] : line[start..end]).Trim();
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out var result) ? result : null;

    private static uint? ParseUInt(string? value) => uint.TryParse(value, out var result) ? result : null;

    private static bool? ParseBool(string? value) => bool.TryParse(value, out var result) ? result : null;
}
