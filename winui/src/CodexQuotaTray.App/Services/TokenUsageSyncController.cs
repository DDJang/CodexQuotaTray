using System.Net.Sockets;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Services;

internal sealed class TokenUsageSyncController(TokenUsageSettingsService settingsService) : IAsyncDisposable
{
    private TokenUsageSyncServer? server;
    private TokenUsageSettings? settings;

    internal string StatusText { get; private set; } = "已关闭";

    internal string AddressText => server?.Address is null ? string.Empty : $"{server.Address}:{server.Port}";

    internal string? PairingInfo => server?.Address is null || settings is null
        ? null
        : $"codexquota://pair?host={server.Address}&port={server.Port}&token={Uri.EscapeDataString(settings.PairingSecret)}";

    internal async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            await StopAsync().ConfigureAwait(false);
            StatusText = "已关闭";
            return;
        }

        if (server is not null)
        {
            return;
        }

        var address = TokenUsageSyncServer.FindPrivateLanAddress();
        if (address is null)
        {
            StatusText = "无可用局域网地址";
            return;
        }

        settings = await settingsService.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var next = new TokenUsageSyncServer(new TokenUsageScanner(), settings.PairingSecret);
        try
        {
            next.Start(address);
            server = next;
            StatusText = "正在监听";
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            await next.DisposeAsync().ConfigureAwait(false);
            StatusText = "端口被占用";
        }
        catch (SocketException)
        {
            await next.DisposeAsync().ConfigureAwait(false);
            StatusText = "无法监听局域网地址";
        }
    }

    internal async Task RegenerateAsync(bool enabled, CancellationToken cancellationToken)
    {
        settings = await settingsService.RegenerateAsync(cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            await StopAsync().ConfigureAwait(false);
            await SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StopAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
        }
    }
}
