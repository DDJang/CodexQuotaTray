using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Relay;

internal sealed class EcsCollector(SnapshotStore store, ILogger<EcsCollector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable("CODEXQUOTA_ECS_COLLECT"), "true", StringComparison.OrdinalIgnoreCase);
        var codex = Environment.GetEnvironmentVariable("CODEXQUOTA_ECS_CODEX") ?? "codex";
        var dataDirectory = Environment.GetEnvironmentVariable("CODEXQUOTA_RELAY_DATA")!;
        var scanner = new TokenUsageScanner(Path.Combine(dataDirectory, "ecs-token-ledger.sqlite3"));
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                if (enabled)
                {
                    var quota = await ReadQuotaAsync(codex, stoppingToken);
                    var usage = await ScanAsync(scanner, stoppingToken);
                    await store.MergeAsync("ecs", RemoteSyncSnapshot.Capture(quota, usage), stoppingToken);
                }
                // Prune expired publications even when neither client requests them.
                _ = await store.ReadAsync("windows", stoppingToken);
                _ = await store.ReadAsync("ecs", stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Relay collection cycle failed ({ErrorType}).", error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<QuotaLanSnapshot?> ReadQuotaAsync(string codex, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = new CodexAppServerClient(new CodexClientOptions(ExplicitCodexBinary: codex));
            _ = await client.ConnectAsync(deadline.Token);
            var result = await client.ReadRateLimitsAsync(deadline.Token);
            var normalized = QuotaNormalizer.Normalize(result);
            return QuotaRuntimeService.ToLanSnapshot(normalized, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("ECS quota capture failed ({ErrorType}).", error.GetType().Name);
            return null;
        }
    }

    private async Task<TokenUsageSnapshot?> ScanAsync(TokenUsageScanner scanner, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
            return Directory.Exists(codexHome)
                ? await scanner.ScanAsync(codexHome, cancellationToken: deadline.Token)
                : null;
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("ECS Token capture failed ({ErrorType}).", error.GetType().Name);
            return null;
        }
    }
}
