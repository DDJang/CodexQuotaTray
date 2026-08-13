using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed class TokenUsageSyncServer : IAsyncDisposable
{
    public const int DefaultPort = 43821;
    private const int MaximumHeaderBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TokenUsageScanner scanner;
    private readonly string secret;
    private readonly Func<QuotaLanSnapshot?> quotaSnapshotProvider;
    private readonly string? codexHome;
    private readonly TimeSpan minimumScanInterval;
    private readonly TimeSpan requestHeaderTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private TcpListener? listener;
    private Task? acceptTask;
    private TokenUsageSnapshot? cached;
    private DateTimeOffset cachedAtUtc;
    private long forcedScanGeneration;

    public TokenUsageSyncServer(
        TokenUsageScanner scanner,
        string secret,
        string? codexHome = null,
        TimeSpan? minimumScanInterval = null,
        Func<QuotaLanSnapshot?>? quotaSnapshotProvider = null,
        TimeSpan? requestHeaderTimeout = null)
    {
        this.scanner = scanner;
        this.secret = secret;
        this.codexHome = codexHome;
        this.minimumScanInterval = minimumScanInterval ?? TimeSpan.FromSeconds(60);
        this.requestHeaderTimeout = requestHeaderTimeout ?? TimeSpan.FromSeconds(10);
        this.quotaSnapshotProvider = quotaSnapshotProvider ?? (() => null);
    }

    public IPAddress? Address { get; private set; }

    public int Port { get; private set; }

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
    }

    public static IPAddress? FindPrivateLanAddress()
    {
        try
        {
            using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            route.Connect(IPAddress.Parse("1.1.1.1"), 53);
            if (route.LocalEndPoint is IPEndPoint endpoint && IsPrivateLanAddress(endpoint.Address))
            {
                return endpoint.Address;
            }
        }
        catch (SocketException)
        {
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(item => item.GetIPProperties())
            .Where(properties => properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)))
            .SelectMany(properties => properties.UnicastAddresses.Select(address => address.Address))
            .FirstOrDefault(IsPrivateLanAddress);
    }

    public static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            && (bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener?.Stop();
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
        }

        lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _ = HandleClientAsync(client, cancellationToken);
        }
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
                    return;
                }

                var snapshot = await GetSnapshotAsync(request.ForceRefresh, cancellationToken).ConfigureAwait(false);
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    snapshot.SchemaVersion,
                    snapshot.GeneratedAtUtc,
                    snapshot.SourceTimeZone,
                    snapshot.Summary,
                    snapshot.Days,
                }, JsonOptions);
                await WriteResponseAsync(stream, 200, "OK", body, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<TokenUsageSnapshot> GetSnapshotAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var forcedGenerationAtRequest = Volatile.Read(ref forcedScanGeneration);
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && cached is not null && now - cachedAtUtc < minimumScanInterval)
        {
            return cached;
        }

        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (forceRefresh && cached is not null && Volatile.Read(ref forcedScanGeneration) != forcedGenerationAtRequest)
            {
                // Another force request that was already in flight completed the
                // required scan while this request waited for the gate.
                return cached;
            }

            if (!forceRefresh && cached is not null && now - cachedAtUtc < minimumScanInterval)
            {
                return cached;
            }

            cached = await scanner.ScanAsync(codexHome, cancellationToken: cancellationToken).ConfigureAwait(false);
            cachedAtUtc = DateTimeOffset.UtcNow;
            if (forceRefresh)
            {
                Interlocked.Increment(ref forcedScanGeneration);
            }
            return cached;
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

    private sealed record Request(string Method, string Path, string? Authorization, bool ForceRefresh);
}
