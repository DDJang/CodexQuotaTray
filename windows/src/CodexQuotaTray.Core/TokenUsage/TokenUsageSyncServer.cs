using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed class TokenUsageSyncServer : IAsyncDisposable
{
    public const int DefaultPort = 43821;
    private const int MaximumHeaderBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<CancellationToken, Task<TokenUsageSnapshot>> scanAsync;
    private readonly string secret;
    private readonly Func<QuotaLanSnapshot?> quotaSnapshotProvider;
    private readonly TimeSpan minimumScanInterval;
    private readonly TimeSpan requestHeaderTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly object cacheLock = new();
    private readonly Action<string> diagnostic;
    private TcpListener? listener;
    private Task? acceptTask;
    private TokenUsageSnapshot? cached;
    private DateTimeOffset cachedAtUtc;
    private long forcedScanGeneration;
    private Task? backgroundRefreshTask;

    public TokenUsageSyncServer(
        TokenUsageScanner scanner,
        string secret,
        string? codexHome = null,
        TimeSpan? minimumScanInterval = null,
        Func<QuotaLanSnapshot?>? quotaSnapshotProvider = null,
        TimeSpan? requestHeaderTimeout = null,
        Action<string>? diagnostic = null)
    {
        scanAsync = cancellationToken => scanner.ScanAsync(codexHome, cancellationToken: cancellationToken);
        this.secret = secret;
        this.minimumScanInterval = minimumScanInterval ?? TimeSpan.FromSeconds(60);
        this.requestHeaderTimeout = requestHeaderTimeout ?? TimeSpan.FromSeconds(10);
        this.quotaSnapshotProvider = quotaSnapshotProvider ?? (() => null);
        this.diagnostic = diagnostic ?? (_ => { });
    }

    internal TokenUsageSyncServer(
        Func<CancellationToken, Task<TokenUsageSnapshot>> scanAsync,
        string secret,
        TimeSpan minimumScanInterval,
        Action<string>? diagnostic = null)
    {
        this.scanAsync = scanAsync;
        this.secret = secret;
        this.minimumScanInterval = minimumScanInterval;
        requestHeaderTimeout = TimeSpan.FromSeconds(10);
        quotaSnapshotProvider = () => null;
        this.diagnostic = diagnostic ?? (_ => { });
    }

    public TokenUsageSyncServer(
        Func<CancellationToken, Task<TokenUsageSnapshot>> readTokenUsageAsync,
        string secret,
        Func<QuotaLanSnapshot?> quotaSnapshotProvider,
        TimeSpan? minimumScanInterval = null,
        TimeSpan? requestHeaderTimeout = null,
        Action<string>? diagnostic = null)
    {
        scanAsync = readTokenUsageAsync;
        this.secret = secret;
        this.minimumScanInterval = minimumScanInterval ?? TimeSpan.FromSeconds(60);
        this.requestHeaderTimeout = requestHeaderTimeout ?? TimeSpan.FromSeconds(10);
        this.quotaSnapshotProvider = quotaSnapshotProvider;
        this.diagnostic = diagnostic ?? (_ => { });
    }

    public IPAddress? Address { get; private set; }

    public int Port { get; private set; }
    public bool IsHealthy => listener is not null && acceptTask is { IsCompleted: false };
    public Exception? ListenerFault => acceptTask?.Exception?.GetBaseException();

    public void Start(IPAddress address, int port = DefaultPort)
    {
        if (listener is not null)
        {
            throw new InvalidOperationException("Token usage sync is already listening.");
        }

        if (!IsPrivateLanAddress(address) && !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("Token usage sync requires a private IPv4 address.", nameof(address));
        }

        listener = new TcpListener(address, port);
        listener.Start();
        Address = address;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptTask = AcceptLoopAsync(listener, lifetime.Token);
        diagnostic($"LAN listener started address={Address}:{Port}");
    }

    public static LanEndpointSelection? FindPrivateLanSelection(Action<string>? diagnostic = null)
    {
        var candidates = new List<LanAddressCandidate>();
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = item.GetIPProperties();
                var gateways = properties.GatewayAddresses
                    .Select(value => value.Address)
                    .Where(value => value.AddressFamily == AddressFamily.InterNetwork && !value.Equals(IPAddress.Any))
                    .ToArray();
                var interfaceIndex = properties.GetIPv4Properties()?.Index;
                if (interfaceIndex is null or < 0) continue;
                foreach (var unicast in properties.UnicastAddresses.Where(value => value.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    candidates.Add(new LanAddressCandidate(
                        unicast.Address,
                        unicast.IPv4Mask,
                        item.NetworkInterfaceType,
                        item.OperationalStatus,
                        gateways,
                        checked((uint)interfaceIndex.Value),
                        interfaceIndex.Value.ToString(),
                        string.Concat(item.Name, " ", item.Description)));
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return SelectPrivateLanSelection(candidates, diagnostic ?? (message => Debug.WriteLine(message)));
    }

    public static IPAddress? FindPrivateLanAddress() => FindPrivateLanSelection()?.Address;

    public static IPAddress? SelectPrivateLanAddress(
        IEnumerable<LanAddressCandidate> candidates,
        Action<string>? diagnostic = null) => SelectPrivateLanSelection(candidates, diagnostic)?.Address;

    public static LanEndpointSelection? SelectPrivateLanSelection(
        IEnumerable<LanAddressCandidate> candidates,
        Action<string>? diagnostic = null)
    {
        var ranked = candidates.Select(candidate => new
        {
            Candidate = candidate,
            Physical = IsPhysicalLanType(candidate.InterfaceType),
            Virtual = HasVirtualOrVpnHint(candidate.Description),
            OnLinkGateway = candidate.Gateways.Any(gateway => SameSubnet(candidate.Address, gateway, candidate.SubnetMask)),
            PrefixLength = PrefixLength(candidate.SubnetMask),
        })
            .ToArray();
        foreach (var item in ranked)
        {
            diagnostic?.Invoke(
                $"LAN candidate interface={item.Candidate.SafeInterfaceId} type={item.Candidate.InterfaceType} " +
                $"address={item.Candidate.Address} private={IsPrivateLanAddress(item.Candidate.Address)} " +
                $"physical={item.Physical} virtualOrVpn={item.Virtual} onLinkGateway={item.OnLinkGateway}");
        }

        var selected = ranked
            .Where(item => item.Candidate.Status == OperationalStatus.Up
                && item.Physical
                && !item.Virtual
                && item.OnLinkGateway
                && IsPrivateLanAddress(item.Candidate.Address))
            .OrderByDescending(item => item.Candidate.InterfaceType == NetworkInterfaceType.Wireless80211)
            .ThenByDescending(item => item.PrefixLength)
            .ThenBy(item => item.Candidate.SafeInterfaceId, StringComparer.Ordinal)
            .Select(item => new LanEndpointSelection(item.Candidate.Address, item.Candidate.InterfaceIndex))
            .FirstOrDefault();
        diagnostic?.Invoke(selected is null ? "LAN address selection failed closed" : $"LAN address selected={selected.Address} interface={selected.InterfaceIndex}");
        return selected;
    }

    public static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            && (bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }

    private static bool IsPhysicalLanType(NetworkInterfaceType type) => type is
        NetworkInterfaceType.Wireless80211 or
        NetworkInterfaceType.Ethernet or
        NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.FastEthernetT;

    private static bool HasVirtualOrVpnHint(string value)
    {
        string[] hints = ["virtual", "hyper-v", "wsl", "vpn", "tunnel", "tap", "tun", "loopback"];
        return hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameSubnet(IPAddress address, IPAddress gateway, IPAddress? mask)
    {
        if (mask is null || address.AddressFamily != AddressFamily.InterNetwork || gateway.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var gatewayBytes = gateway.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        return Enumerable.Range(0, 4).All(index =>
            (addressBytes[index] & maskBytes[index]) == (gatewayBytes[index] & maskBytes[index]));
    }

    private static int PrefixLength(IPAddress? mask) => mask?.GetAddressBytes().Sum(value => System.Numerics.BitOperations.PopCount(value)) ?? 0;

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener?.Stop();
        diagnostic($"LAN listener stop requested address={Address}:{Port}");
        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                diagnostic($"LAN listener completion fault={error.GetType().Name}");
            }
        }

        lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            diagnostic("LAN listener accepted connection");
            _ = ObserveClientAsync(HandleClientAsync(client, cancellationToken));
        }
    }

    private async Task ObserveClientAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) { diagnostic($"LAN client handler fault={error.GetType().Name}"); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                await using var stream = client.GetStream();
                Request? request;
                using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    requestTimeout.CancelAfter(requestHeaderTimeout);
                    try
                    {
                        request = await ReadRequestAsync(stream, requestTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
                if (request is null)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", null, cancellationToken).ConfigureAwait(false);
                    return;
                }
                diagnostic($"LAN request path={request.Path}");

                if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(request.Path, "/v1/token-usage", StringComparison.Ordinal)
                    && !string.Equals(request.Path, "/v1/quota", StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!Authorized(request.Authorization))
                {
                    await WriteResponseAsync(stream, 401, "Unauthorized", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request.Path, "/v1/quota", StringComparison.Ordinal))
                {
                    var quota = quotaSnapshotProvider();
                    if (quota is null)
                    {
                        await WriteResponseAsync(stream, 503, "Service Unavailable", null, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var quotaBody = JsonSerializer.SerializeToUtf8Bytes(quota, JsonOptions);
                    await WriteResponseAsync(stream, 200, "OK", quotaBody, cancellationToken).ConfigureAwait(false);
                    diagnostic("LAN response path=/v1/quota status=200");
                    return;
                }

                var snapshot = await GetSnapshotAsync(request.ForceRefresh, cancellationToken).ConfigureAwait(false);
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    snapshot.SchemaVersion,
                    snapshot.GeneratedAtUtc,
                    snapshot.SourceTimeZone,
                    Source = snapshot.Source switch
                    {
                        TokenUsageDataSource.Local => "Local",
                        TokenUsageDataSource.CodexCli => "CodexCli",
                        TokenUsageDataSource.OAuth => "OAuth",
                        _ => "Local",
                    },
                    Scope = snapshot.Source == TokenUsageDataSource.Local ? "Local" : "Account",
                    Summary = ProjectSummary(snapshot),
                    snapshot.Days,
                }, JsonOptions);
                await WriteResponseAsync(stream, 200, "OK", body, cancellationToken).ConfigureAwait(false);
                diagnostic("LAN response path=/v1/token-usage status=200");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<TokenUsageSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        TokenUsageSnapshot? available;
        DateTimeOffset availableAt;
        lock (cacheLock)
        {
            available = cached;
            availableAt = cachedAtUtc;
        }
        if (!forceRefresh && available is not null)
        {
            if (DateTimeOffset.UtcNow - availableAt >= minimumScanInterval) StartBackgroundRefresh();
            return available;
        }

        return await RefreshSnapshotAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private void StartBackgroundRefresh()
    {
        lock (cacheLock)
        {
            if (backgroundRefreshTask is { IsCompleted: false }) return;
            backgroundRefreshTask = RunBackgroundRefreshAsync();
        }
    }

    private async Task RunBackgroundRefreshAsync()
    {
        try { _ = await RefreshSnapshotAsync(false, lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { diagnostic($"Token background refresh fault={error.GetType().Name}"); }
    }

    private async Task<TokenUsageSnapshot> RefreshSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var forcedGenerationAtRequest = Volatile.Read(ref forcedScanGeneration);

        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TokenUsageSnapshot? current;
            DateTimeOffset currentAt;
            lock (cacheLock) { current = cached; currentAt = cachedAtUtc; }
            if (forceRefresh && current is not null && Volatile.Read(ref forcedScanGeneration) != forcedGenerationAtRequest)
            {
                // Another force request that was already in flight completed the
                // required scan while this request waited for the gate.
                return current;
            }

            if (!forceRefresh && current is not null && DateTimeOffset.UtcNow - currentAt < minimumScanInterval)
            {
                return current;
            }

            var refreshed = await scanAsync(cancellationToken).ConfigureAwait(false);
            lock (cacheLock)
            {
                cached = refreshed;
                cachedAtUtc = DateTimeOffset.UtcNow;
            }
            if (forceRefresh)
            {
                Interlocked.Increment(ref forcedScanGeneration);
            }
            return refreshed;
        }
        finally
        {
            scanGate.Release();
        }
    }

    private bool Authorized(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var actual = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(secret);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumHeaderBytes);
        try
        {
            var length = 0;
            while (length < MaximumHeaderBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, MaximumHeaderBytes - length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (HeaderComplete(buffer.AsSpan(0, length)))
                {
                    break;
                }
            }

            if (length == 0 || !HeaderComplete(buffer.AsSpan(0, length)))
            {
                return null;
            }

            var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                return null;
            }

            string? authorization = null;
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 && line[..separator].Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = line[(separator + 1)..].Trim();
                }
            }

            var (path, forceRefresh) = ParseTarget(requestLine[1]);
            return new Request(requestLine[0], path, authorization, forceRefresh);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HeaderComplete(ReadOnlySpan<byte> value) => value.IndexOf("\r\n\r\n"u8) >= 0;

    private static (string Path, bool ForceRefresh) ParseTarget(string target)
    {
        var queryStart = target.IndexOf('?');
        if (queryStart < 0)
        {
            return (target, false);
        }

        var path = target[..queryStart];
        var query = target[(queryStart + 1)..];
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            if (key.Equals("refresh", StringComparison.OrdinalIgnoreCase)
                && value.Equals("force", StringComparison.OrdinalIgnoreCase))
            {
                return (path, true);
            }
        }

        return (path, false);
    }

    private static async Task WriteResponseAsync(Stream stream, int code, string reason, byte[]? body, CancellationToken cancellationToken)
    {
        body ??= [];
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TokenUsageLanSummary ProjectSummary(TokenUsageSnapshot snapshot)
    {
        var summary = snapshot.Summary;
        var available = snapshot.AvailableMetrics;
        return new TokenUsageLanSummary(
            TodayTokens: HasMetric(available, TokenUsageMetricAvailability.Today) ? summary.TodayTokens : null,
            Last7DaysTokens: HasMetric(available, TokenUsageMetricAvailability.Last7Days) ? summary.Last7DaysTokens : null,
            Last30DaysTokens: HasMetric(available, TokenUsageMetricAvailability.Last30Days) ? summary.Last30DaysTokens : null,
            LifetimeTokens: HasMetric(available, TokenUsageMetricAvailability.Lifetime) ? summary.LifetimeTokens : null,
            PeakDailyTokens: HasMetric(available, TokenUsageMetricAvailability.Peak) ? summary.PeakDailyTokens : null,
            PeakDate: HasMetric(available, TokenUsageMetricAvailability.Peak) ? summary.PeakDate : null,
            ActiveDays: summary.ActiveDays,
            CurrentStreak: HasMetric(available, TokenUsageMetricAvailability.CurrentStreak) ? summary.CurrentStreak : null,
            LongestStreak: HasMetric(available, TokenUsageMetricAvailability.LongestStreak) ? summary.LongestStreak : null);
    }

    private static bool HasMetric(TokenUsageMetricAvailability available, TokenUsageMetricAvailability metric) =>
        (available & metric) == metric;

    private sealed record Request(string Method, string Path, string? Authorization, bool ForceRefresh);

    private sealed record TokenUsageLanSummary(
        long? TodayTokens,
        long? Last7DaysTokens,
        long? Last30DaysTokens,
        long? LifetimeTokens,
        long? PeakDailyTokens,
        DateOnly? PeakDate,
        int ActiveDays,
        int? CurrentStreak,
        int? LongestStreak);
}
