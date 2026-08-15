using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Services;

internal sealed class TokenUsageSyncController : IAsyncDisposable
{
    private static readonly TimeSpan DefaultAddressCheckInterval = TimeSpan.FromSeconds(15);
    private readonly Func<CancellationToken, Task<TokenUsageSettings>> loadSettings;
    private readonly Func<CancellationToken, Task<TokenUsageSettings>> regenerateSettings;
    private readonly Func<IPAddress?> addressProvider;
    private readonly Func<string, ILanSyncServer> serverFactory;
    private readonly Func<Guid, string, IDnsSdPublisher> publisherFactory;
    private readonly TimeSpan addressCheckInterval;
    private readonly Action<string> diagnostic;
    private readonly int port;
    private readonly string displayNameSuffix;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private ILanSyncServer? server;
    private IDnsSdPublisher? publisher;
    private TokenUsageSettings? settings;
    private CancellationTokenSource? monitorLifetime;
    private Task? monitorTask;
    private bool enabled;
    private string displayName;

    internal TokenUsageSyncController(
        TokenUsageSettingsService settingsService,
        Func<QuotaLanSnapshot?> quotaSnapshotProvider,
        int port = TokenUsageSyncServer.DefaultPort,
        string displayNameSuffix = "",
        string dnsSdInstancePrefix = "CodexQuotaTray")
        : this(
            settingsService.LoadOrCreateAsync,
            settingsService.RegenerateAsync,
            TokenUsageSyncServer.FindPrivateLanAddress,
            secret => new LanSyncServerAdapter(new TokenUsageSyncServer(new TokenUsageScanner(), secret, quotaSnapshotProvider: quotaSnapshotProvider)),
            (deviceId, name) => new DnsSdPublisherAdapter(new DnsSdServicePublisher(deviceId, name, dnsSdInstancePrefix)),
            port,
            displayNameSuffix,
            DefaultAddressCheckInterval,
            message => System.Diagnostics.Debug.WriteLine(message))
    {
    }

    internal TokenUsageSyncController(
        Func<CancellationToken, Task<TokenUsageSettings>> loadSettings,
        Func<CancellationToken, Task<TokenUsageSettings>> regenerateSettings,
        Func<IPAddress?> addressProvider,
        Func<string, ILanSyncServer> serverFactory,
        Func<Guid, string, IDnsSdPublisher> publisherFactory,
        int port,
        string displayNameSuffix,
        TimeSpan addressCheckInterval,
        Action<string>? diagnostic = null)
    {
        this.loadSettings = loadSettings;
        this.regenerateSettings = regenerateSettings;
        this.addressProvider = addressProvider;
        this.serverFactory = serverFactory;
        this.publisherFactory = publisherFactory;
        this.port = port;
        this.displayNameSuffix = displayNameSuffix;
        this.addressCheckInterval = addressCheckInterval;
        this.diagnostic = diagnostic ?? (_ => { });
        displayName = CreateDisplayName(displayNameSuffix);
    }

    internal string StatusText { get; private set; } = "已关闭";
    internal string AddressText => server?.Address is null ? string.Empty : $"{server.Address}:{server.Port}";
    internal string DeviceNameText => server is null ? string.Empty : displayName;
    internal string? PairingInfo => server?.Address is null || settings is null
        ? null
        : new TokenUsagePairing(settings.DeviceId, server.Address.ToString(), server.Port, settings.PairingSecret, displayName).ToUri();
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

        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.enabled) return;
            var loadedSettings = await loadSettings(cancellationToken).ConfigureAwait(false);
            settings = loadedSettings;
            displayName = CreateDisplayName(displayNameSuffix);
            monitorLifetime = new CancellationTokenSource();
            this.enabled = true;
            monitorTask = MonitorAddressAsync(monitorLifetime.Token);
            var address = addressProvider();
            if (address is null)
            {
                SetStatus("无可用局域网地址");
                diagnostic($"LAN listener unavailable; retry in {addressCheckInterval.TotalSeconds:0.###}s");
            }
            else
            {
                await TryStartServerAsync(address, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            stateGate.Release();
        }
    }

    internal async Task RegenerateAsync(bool enabled, CancellationToken cancellationToken)
    {
        settings = await regenerateSettings(cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            await StopAsync().ConfigureAwait(false);
            await SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
        }
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        stateGate.Dispose();
    }

    private async Task<bool> TryStartServerAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var nextServer = serverFactory(settings!.PairingSecret);
        try
        {
            nextServer.Start(address, port);
            server = nextServer;
            diagnostic($"LAN listener started address={address}:{nextServer.Port}");
            await TryStartPublisherAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException error)
        {
            await nextServer.DisposeAsync().ConfigureAwait(false);
            var status = error.SocketErrorCode == SocketError.AddressAlreadyInUse ? "端口被占用" : "无法监听局域网地址";
            SetStatus(status);
            diagnostic($"LAN listener start/restart failure={error.SocketErrorCode}; retry in {addressCheckInterval.TotalSeconds:0.###}s");
            return false;
        }
    }

    private async Task TryStartPublisherAsync(CancellationToken cancellationToken)
    {
        if (server is null || publisher is not null) return;
        IDnsSdPublisher? nextPublisher = null;
        try
        {
            nextPublisher = publisherFactory(settings!.DeviceId, displayName);
            await nextPublisher.StartAsync(server.Address!, server.Port, cancellationToken).ConfigureAwait(false);
            publisher = nextPublisher;
            SetStatus("正在监听");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            if (nextPublisher is not null) await nextPublisher.DisposeAsync().ConfigureAwait(false);
            SetStatus("正在监听（自动发现不可用）");
            diagnostic($"DNS-SD publisher unavailable={error.GetType().Name}; retry in {addressCheckInterval.TotalSeconds:0.###}s");
        }
    }

    private async Task MonitorAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(addressCheckInterval, cancellationToken).ConfigureAwait(false);
                var address = addressProvider();
                await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!enabled) continue;
                    if (address is null)
                    {
                        if (server is not null) await StopListenerResourcesAsync().ConfigureAwait(false);
                        SetStatus("无可用局域网地址");
                        diagnostic($"LAN address unavailable; retry in {addressCheckInterval.TotalSeconds:0.###}s");
                    }
                    else if (server is null)
                    {
                        await TryStartServerAsync(address, cancellationToken).ConfigureAwait(false);
                    }
                    else if (!address.Equals(server.Address))
                    {
                        var oldAddress = server.Address;
                        diagnostic($"LAN address change {oldAddress} -> {address}");
                        await StopListenerResourcesAsync().ConfigureAwait(false);
                        if (!await TryStartServerAsync(address, cancellationToken).ConfigureAwait(false))
                        {
                            SetStatus("网络地址变化，等待重新监听");
                        }
                    }
                    else if (publisher is null)
                    {
                        await TryStartPublisherAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally { stateGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task StopAsync()
    {
        enabled = false;
        var monitor = monitorLifetime;
        monitorLifetime = null;
        monitor?.Cancel();
        if (monitorTask is not null)
        {
            try { await monitorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            monitorTask = null;
        }
        await stateGate.WaitAsync().ConfigureAwait(false);
        try { await StopListenerResourcesAsync().ConfigureAwait(false); }
        finally
        {
            stateGate.Release();
            monitor?.Dispose();
        }
    }

    private async Task StopListenerResourcesAsync()
    {
        if (publisher is not null)
        {
            await publisher.DisposeAsync().ConfigureAwait(false);
            publisher = null;
        }
        if (server is not null)
        {
            diagnostic($"LAN listener stopped address={server.Address}:{server.Port}");
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
        }
    }

    private void SetStatus(string value)
    {
        StatusText = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string CreateDisplayName(string suffix) => string.Concat(Environment.MachineName, suffix);
}

internal interface ILanSyncServer : IAsyncDisposable
{
    IPAddress? Address { get; }
    int Port { get; }
    void Start(IPAddress address, int port);
}

internal sealed class LanSyncServerAdapter(TokenUsageSyncServer inner) : ILanSyncServer
{
    public IPAddress? Address => inner.Address;
    public int Port => inner.Port;
    public void Start(IPAddress address, int port) => inner.Start(address, port);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal interface IDnsSdPublisher : IAsyncDisposable
{
    Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken);
}

internal sealed class DnsSdPublisherAdapter(DnsSdServicePublisher inner) : IDnsSdPublisher
{
    public Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken) => inner.StartAsync(address, port, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
