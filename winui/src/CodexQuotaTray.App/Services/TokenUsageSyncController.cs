using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Services;

internal sealed class TokenUsageSyncController(
    TokenUsageSettingsService settingsService,
    Func<QuotaLanSnapshot?> quotaSnapshotProvider,
    int port = TokenUsageSyncServer.DefaultPort,
    string displayNameSuffix = "",
    string dnsSdInstancePrefix = "CodexQuotaTray") : IAsyncDisposable
{
    private static readonly TimeSpan AddressCheckInterval = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private TokenUsageSyncServer? server;
    private DnsSdServicePublisher? publisher;
    private TokenUsageSettings? settings;
    private CancellationTokenSource? monitorLifetime;
    private Task? monitorTask;
    private string displayName = CreateDisplayName(displayNameSuffix);

    internal string StatusText { get; private set; } = "已关闭";

    internal string AddressText => server?.Address is null ? string.Empty : $"{server.Address}:{server.Port}";

    internal string DeviceNameText => server is null ? string.Empty : displayName;

    internal string? PairingInfo => server?.Address is null || settings is null
        ? null
        : new TokenUsagePairing(
            settings.DeviceId,
            server.Address.ToString(),
            server.Port,
            settings.PairingSecret,
            displayName).ToUri();

    internal event EventHandler? Changed;

    internal async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            await StopAsync().ConfigureAwait(false);
            StatusText = "已关闭";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var address = TokenUsageSyncServer.FindPrivateLanAddress();
        if (address is null)
        {
            StatusText = "无可用局域网地址";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (server is not null)
            {
                return;
            }

            settings = await settingsService.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            displayName = CreateDisplayName(displayNameSuffix);
            await StartCoreAsync(address, cancellationToken).ConfigureAwait(false);
            monitorLifetime = new CancellationTokenSource();
            monitorTask = MonitorAddressAsync(monitorLifetime.Token);
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            StatusText = "端口被占用";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (SocketException)
        {
            StatusText = "无法监听局域网地址";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            stateGate.Release();
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
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        stateGate.Dispose();
    }

    private async Task StartCoreAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var nextServer = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            settings!.PairingSecret,
            quotaSnapshotProvider: quotaSnapshotProvider);
        try
        {
            nextServer.Start(address, port);
        }
        catch
        {
            await nextServer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        DnsSdServicePublisher? nextPublisher = null;
        try
        {
            nextPublisher = new DnsSdServicePublisher(settings.DeviceId, displayName, dnsSdInstancePrefix);
            nextPublisher.Start(address, nextServer.Port);
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException or Win32Exception or InvalidOperationException)
        {
            if (nextPublisher is not null)
            {
                await nextPublisher.DisposeAsync().ConfigureAwait(false);
            }

            nextPublisher = null;
        }

        server = nextServer;
        publisher = nextPublisher;
        StatusText = nextPublisher is null ? "正在监听（自动发现不可用）" : "正在监听";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task MonitorAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AddressCheckInterval, cancellationToken).ConfigureAwait(false);
                var address = TokenUsageSyncServer.FindPrivateLanAddress();
                if (address is null)
                {
                    continue;
                }

                await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (server is not null && !address.Equals(server.Address))
                    {
                        await RestartAtAddressAsync(address, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    stateGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RestartAtAddressAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var previousPublisher = publisher;
        var previousServer = server;
        publisher = null;
        server = null;
        if (previousPublisher is not null)
        {
            await previousPublisher.DisposeAsync().ConfigureAwait(false);
        }

        if (previousServer is not null)
        {
            await previousServer.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await StartCoreAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            StatusText = "网络地址变化，等待重新监听";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task StopAsync()
    {
        var monitor = monitorLifetime;
        monitorLifetime = null;
        monitor?.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            monitorTask = null;
        }

        await stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (publisher is not null)
            {
                await publisher.DisposeAsync().ConfigureAwait(false);
                publisher = null;
            }

            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                server = null;
            }
        }
        finally
        {
            stateGate.Release();
            monitor?.Dispose();
        }
    }

    private static string CreateDisplayName(string suffix) => string.Concat(Environment.MachineName, suffix);
}
