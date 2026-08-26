using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.App.Services;

internal sealed class TokenUsageSyncController : IAsyncDisposable
{
    private static readonly TimeSpan DefaultAddressCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultNetworkChangeDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultRepairCooldown = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan RepairProbeTimeout = TimeSpan.FromMilliseconds(750);
    private readonly Func<CancellationToken, Task<TokenUsageSettings>> loadSettings;
    private readonly Func<CancellationToken, Task<TokenUsageSettings>> regenerateSettings;
    private readonly Func<LanEndpointSelection?> addressProvider;
    private readonly Func<string, ILanSyncServer> serverFactory;
    private readonly Func<Guid, string, IDnsSdPublisher> publisherFactory;
    private readonly TimeSpan addressCheckInterval;
    private readonly Action<string> diagnostic;
    private readonly Func<LanDiagnosticState>? diagnosticStateProvider;
    private readonly TimeSpan networkChangeDebounce;
    private readonly Func<IPAddress, CancellationToken, Task<LanRepairProbeResult>> repairProbe;
    private readonly Func<IPAddress, LanEndpointSelection, bool> repairRouteValidator;
    private readonly int port;
    private readonly string displayNameSuffix;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private ILanSyncServer? server;
    private IDnsSdPublisher? publisher;
    private TokenUsageSettings? settings;
    private CancellationTokenSource? monitorLifetime;
    private Task? monitorTask;
    private bool enabled;
    private uint currentInterfaceIndex;
    private string displayName;
    private readonly object networkChangeGate = new();
    private CancellationTokenSource? networkChangeLifetime;
    private DateTimeOffset? lastRepairAtUtc;
    private int repairInFlight;

    internal TokenUsageSyncController(
        TokenUsageSettingsService settingsService,
        Func<CancellationToken, Task<TokenUsageSnapshot>> readTokenUsageAsync,
        Func<QuotaLanSnapshot?> quotaSnapshotProvider,
        int port = TokenUsageSyncServer.DefaultPort,
        string displayNameSuffix = "",
        string dnsSdInstancePrefix = "CodexQuotaTray",
        Action<string>? diagnostic = null,
        Func<LanDiagnosticState>? diagnosticStateProvider = null,
        TimeSpan? networkChangeDebounce = null,
        Func<IPAddress, CancellationToken, Task<LanRepairProbeResult>>? repairProbe = null,
        Func<IPAddress, LanEndpointSelection, bool>? repairRouteValidator = null)
    {
        loadSettings = settingsService.LoadOrCreateAsync;
        regenerateSettings = settingsService.RegenerateAsync;
        addressProvider = () => TokenUsageSyncServer.FindPrivateLanSelection(diagnostic);
        serverFactory = secret => new LanSyncServerAdapter(new TokenUsageSyncServer(
            readTokenUsageAsync,
            secret,
            quotaSnapshotProvider,
            diagnostic: diagnostic,
            requestObserved: () => Changed?.Invoke(this, EventArgs.Empty)));
        publisherFactory = (deviceId, name) => new DnsSdPublisherAdapter(
            new DnsSdServicePublisher(deviceId, name, dnsSdInstancePrefix, diagnostic: diagnostic));
        this.port = port;
        this.displayNameSuffix = displayNameSuffix;
        addressCheckInterval = DefaultAddressCheckInterval;
        this.diagnostic = diagnostic ?? (message => System.Diagnostics.Debug.WriteLine(message));
        this.diagnosticStateProvider = diagnosticStateProvider;
        this.networkChangeDebounce = networkChangeDebounce ?? DefaultNetworkChangeDebounce;
        this.repairProbe = repairProbe ?? ProbeOnceAsync;
        this.repairRouteValidator = repairRouteValidator ?? TokenUsageSyncServer.HasOnLinkPrivateLanRoute;
        displayName = CreateDisplayName(displayNameSuffix);
    }

    internal TokenUsageSyncController(
        Func<CancellationToken, Task<TokenUsageSettings>> loadSettings,
        Func<CancellationToken, Task<TokenUsageSettings>> regenerateSettings,
        Func<LanEndpointSelection?> addressProvider,
        Func<string, ILanSyncServer> serverFactory,
        Func<Guid, string, IDnsSdPublisher> publisherFactory,
        int port,
        string displayNameSuffix,
        TimeSpan addressCheckInterval,
        Action<string>? diagnostic = null,
        Func<LanDiagnosticState>? diagnosticStateProvider = null,
        TimeSpan? networkChangeDebounce = null,
        Func<IPAddress, CancellationToken, Task<LanRepairProbeResult>>? repairProbe = null,
        Func<IPAddress, LanEndpointSelection, bool>? repairRouteValidator = null)
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
        this.diagnosticStateProvider = diagnosticStateProvider;
        this.networkChangeDebounce = networkChangeDebounce ?? DefaultNetworkChangeDebounce;
        this.repairProbe = repairProbe ?? ProbeOnceAsync;
        this.repairRouteValidator = repairRouteValidator ?? TokenUsageSyncServer.HasOnLinkPrivateLanRoute;
        displayName = CreateDisplayName(displayNameSuffix);
    }

    internal string StatusText { get; private set; } = "已关闭";
    internal string AddressText => server?.Address is null ? string.Empty : $"{server.Address}:{server.Port}";
    internal string DeviceNameText => server is null ? string.Empty : displayName;
    internal string? PairingInfo => server?.Address is null || settings is null
        ? null
        : new TokenUsagePairing(settings.DeviceId, server.Address.ToString(), server.Port, settings.PairingSecret, displayName).ToUri();
    internal string MobileStatusText
    {
        get
        {
            var snapshot = diagnosticStateProvider?.Invoke();
            if (snapshot?.LastRequestResult is { } result)
            {
                return result.ToUpperInvariant() switch
                {
                    "SUCCESS" => snapshot.LastSuccessUtc is { } success
                        ? $"最近手机连接：{success.ToLocalTime():MM-dd HH:mm}"
                        : "最近手机连接：已成功",
                    "AUTH_FAILED" => "最近手机请求：认证失败",
                    _ => "最近手机请求：请求失败",
                };
            }

            return "尚未收到手机连接";
        }
    }
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
            diagnostic($"LAN pairing device={settings.DeviceId:D} endpoint=unavailable");
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

    internal void OnNetworkChanged(string reason = "NETWORK_EVENT")
    {
        if (!enabled)
        {
            return;
        }

        diagnostic($"Network change observed reason={reason}");
        SetStatus("正在重新连接…");
        CancellationTokenSource next;
        lock (networkChangeGate)
        {
            networkChangeLifetime?.Cancel();
            networkChangeLifetime?.Dispose();
            next = new CancellationTokenSource();
            networkChangeLifetime = next;
        }

        diagnostic($"LAN reconcile scheduled reason={reason} debounceMs={networkChangeDebounce.TotalMilliseconds:0}");
        _ = ReconcileAfterNetworkChangeAsync(reason, next);
    }

    internal async Task<string> RepairPhoneConnectionAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref repairInFlight, 1, 0) != 0)
        {
            diagnostic("LAN repair cooldown reason=IN_FLIGHT");
            return "正在尝试修复，请稍候";
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (networkChangeGate)
            {
                if (lastRepairAtUtc is { } previous && now - previous < DefaultRepairCooldown)
                {
                    diagnostic("LAN repair cooldown reason=TOO_SOON");
                    return "请稍后再试";
                }
            }

            var snapshot = diagnosticStateProvider?.Invoke();
            var remoteText = snapshot?.LastSuccessfulRemoteAddress
                ?? (string.Equals(snapshot?.LastRequestResult, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                    ? snapshot?.LastRemoteAddress
                    : null);
            diagnostic($"LAN repair requested remote={remoteText ?? "unavailable"}");
            if (!IPAddress.TryParse(remoteText, out var remote)
                || remote.AddressFamily != AddressFamily.InterNetwork
                || !TokenUsageSyncServer.IsAllowedRepairAddress(remote))
            {
                diagnostic("LAN repair skipped reason=NO_VALID_SUCCESSFUL_REMOTE");
                return "暂无可修复的手机连接记录";
            }

            LanEndpointSelection? selection;
            IPAddress? listenerAddress;
            uint listenerInterfaceIndex;
            var listenerHealthy = false;
            await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                selection = addressProvider();
                listenerAddress = server?.Address;
                listenerInterfaceIndex = currentInterfaceIndex;
                listenerHealthy = enabled && server?.IsHealthy == true;
            }
            finally
            {
                stateGate.Release();
            }

            if (selection is null
                || !listenerHealthy
                || listenerAddress is null
                || !TokenUsageSyncServer.IsPrivateLanAddress(listenerAddress)
                || !listenerAddress.Equals(selection.Address)
                || listenerInterfaceIndex != selection.InterfaceIndex
                || !repairRouteValidator(remote, selection))
            {
                diagnostic($"LAN repair skipped reason=REMOTE_NOT_ON_LINK remote={remote}");
                return "手机网络已变化，请先在手机端重新刷新";
            }

            lock (networkChangeGate) lastRepairAtUtc = now;
            diagnostic($"LAN repair probe started remote={remote}");
            var probeResult = LanRepairProbeResult.IO;
            try
            {
                probeResult = await repairProbe(remote, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                diagnostic($"LAN repair probe exception exceptionClass={error.GetType().Name}");
            }

            diagnostic($"LAN repair probe completed remote={remote} probeResult={probeResult} actionResult=PROBE_SENT");
            return "已尝试修复，请在手机端重新刷新";
        }
        finally
        {
            Volatile.Write(ref repairInFlight, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (networkChangeGate)
        {
            networkChangeLifetime?.Cancel();
            networkChangeLifetime?.Dispose();
            networkChangeLifetime = null;
        }
        await StopAsync().ConfigureAwait(false);
        stateGate.Dispose();
    }

    private async Task<bool> TryStartServerAsync(LanEndpointSelection selection, CancellationToken cancellationToken)
    {
        var nextServer = serverFactory(settings!.PairingSecret);
        try
        {
            diagnostic($"LAN listener start bind={selection.Address} port={port} interfaceIndex={selection.InterfaceIndex}");
            nextServer.Start(selection.Address, port);
            server = nextServer;
            currentInterfaceIndex = selection.InterfaceIndex;
            diagnostic($"LAN pairing device={settings.DeviceId:D} endpoint={selection.Address}:{nextServer.Port}");
            diagnostic($"LAN listener healthy=true bind={selection.Address} port={nextServer.Port} interfaceIndex={selection.InterfaceIndex}");
            diagnostic($"LAN listener started address={selection.Address}:{nextServer.Port} interface={selection.InterfaceIndex}");
            await TryStartPublisherAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException error)
        {
            await nextServer.DisposeAsync().ConfigureAwait(false);
            var status = error.SocketErrorCode == SocketError.AddressAlreadyInUse ? "端口被占用" : "无法监听局域网地址";
            SetStatus(status);
            diagnostic($"LAN listener healthy=false bind={selection.Address} port={port} interfaceIndex={selection.InterfaceIndex} restartReason={error.SocketErrorCode}");
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
            await nextPublisher.StartAsync(server.Address!, server.Port, currentInterfaceIndex, cancellationToken).ConfigureAwait(false);
            publisher = nextPublisher;
            diagnostic($"DNS-SD register success interface={currentInterfaceIndex}");
            SetStatus("正在监听");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            if (nextPublisher is not null) await nextPublisher.DisposeAsync().ConfigureAwait(false);
            SetStatus("正在监听（自动发现不可用）");
            diagnostic($"DNS-SD register failure interface={currentInterfaceIndex} exceptionClass={error.GetType().Name}");
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
                await ReconcileAsync("PERIODIC", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReconcileAfterNetworkChangeAsync(string reason, CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(networkChangeDebounce, scheduled.Token).ConfigureAwait(false);
            await ReconcileAsync(reason, scheduled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            diagnostic($"LAN reconcile result=failed reason={reason} exceptionClass={error.GetType().Name}");
        }
        finally
        {
            lock (networkChangeGate)
            {
                if (ReferenceEquals(networkChangeLifetime, scheduled)) networkChangeLifetime = null;
            }
            scheduled.Dispose();
        }
    }

    private async Task ReconcileAsync(string reason, CancellationToken cancellationToken)
    {
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!enabled) return;
            var address = addressProvider();
            if (address is null)
            {
                if (server is not null) await StopListenerResourcesAsync().ConfigureAwait(false);
                SetStatus("无可用局域网地址");
                diagnostic($"LAN address unavailable; retry in {addressCheckInterval.TotalSeconds:0.###}s");
                diagnostic($"LAN reconcile result=unavailable reason={reason}");
            }
            else if (server is null)
            {
                var started = await TryStartServerAsync(address, cancellationToken).ConfigureAwait(false);
                diagnostic($"LAN reconcile result={(started ? "started" : "restart-failed")} reason={reason}");
            }
            else if (!address.Address.Equals(server.Address) || address.InterfaceIndex != currentInterfaceIndex)
            {
                var oldAddress = server.Address;
                diagnostic($"LAN selection change {oldAddress}/interface={currentInterfaceIndex} -> {address.Address}/interface={address.InterfaceIndex}");
                await StopListenerResourcesAsync().ConfigureAwait(false);
                var restarted = await TryStartServerAsync(address, cancellationToken).ConfigureAwait(false);
                if (!restarted) SetStatus("网络地址变化，等待重新监听");
                diagnostic($"LAN reconcile result={(restarted ? "restarted" : "restart-failed")} reason={reason}");
            }
            else if (!server.IsHealthy)
            {
                var fault = server.ListenerFault?.GetType().Name ?? "Completed";
                diagnostic($"LAN listener healthy=false bind={server.Address} port={server.Port} interfaceIndex={currentInterfaceIndex} restartReason={fault}");
                diagnostic($"LAN listener unhealthy fault={fault}; retry in {addressCheckInterval.TotalSeconds:0.###}s");
                await StopListenerResourcesAsync().ConfigureAwait(false);
                var restarted = await TryStartServerAsync(address, cancellationToken).ConfigureAwait(false);
                diagnostic($"LAN reconcile result={(restarted ? "restarted" : "restart-failed")} reason={reason}");
            }
            else if (publisher is null)
            {
                await TryStartPublisherAsync(cancellationToken).ConfigureAwait(false);
                diagnostic($"LAN reconcile result=dns-sd-retry reason={reason}");
            }
            else
            {
                SetStatus("正在监听");
                diagnostic($"LAN reconcile result=no-change reason={reason}");
            }
        }
        finally
        {
            stateGate.Release();
        }
    }

    private async Task StopAsync()
    {
        enabled = false;
        lock (networkChangeGate)
        {
            networkChangeLifetime?.Cancel();
            networkChangeLifetime?.Dispose();
            networkChangeLifetime = null;
        }
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
            diagnostic($"LAN listener healthy=false bind={server.Address} port={server.Port} interfaceIndex={currentInterfaceIndex} restartReason=stopped");
            diagnostic($"LAN listener stopped address={server.Address}:{server.Port}");
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
            currentInterfaceIndex = 0;
        }
    }

    private void SetStatus(string value)
    {
        StatusText = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string CreateDisplayName(string suffix) => string.Concat(Environment.MachineName, suffix);

    private static async Task<LanRepairProbeResult> ProbeOnceAsync(
        IPAddress remote,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(
                remote,
                RepairProbeTimeout,
                Array.Empty<byte>(),
                new PingOptions(),
                cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? LanRepairProbeResult.REPLY
                : LanRepairProbeResult.TIMEOUT;
        }
        catch (Exception error) when (error is PingException or SocketException)
        {
            return LanRepairProbeResult.IO;
        }
        catch (OperationCanceledException)
        {
            return LanRepairProbeResult.IO;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return LanRepairProbeResult.IO;
        }
    }
}

internal enum LanRepairProbeResult
{
    REPLY,
    TIMEOUT,
    IO,
}

internal interface ILanSyncServer : IAsyncDisposable
{
    IPAddress? Address { get; }
    int Port { get; }
    bool IsHealthy { get; }
    Exception? ListenerFault { get; }
    void Start(IPAddress address, int port);
}

internal sealed class LanSyncServerAdapter(TokenUsageSyncServer inner) : ILanSyncServer
{
    public IPAddress? Address => inner.Address;
    public int Port => inner.Port;
    public bool IsHealthy => inner.IsHealthy;
    public Exception? ListenerFault => inner.ListenerFault;
    public void Start(IPAddress address, int port) => inner.Start(address, port);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal interface IDnsSdPublisher : IAsyncDisposable
{
    Task StartAsync(IPAddress address, int port, uint interfaceIndex, CancellationToken cancellationToken);
}

internal sealed class DnsSdPublisherAdapter(DnsSdServicePublisher inner) : IDnsSdPublisher
{
    public Task StartAsync(IPAddress address, int port, uint interfaceIndex, CancellationToken cancellationToken) => inner.StartAsync(address, port, interfaceIndex, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
