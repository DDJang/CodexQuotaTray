using System.Diagnostics;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;

namespace CodexQuotaTray.App.Services;

internal sealed class SettingsPlatformActions : ISettingsPlatformActions
{
    private readonly PreviewDataPaths paths;
    private readonly PreviewPersistence persistence;
    private readonly TokenUsageSyncController tokenSync;
    private readonly Func<bool> tokenSyncEnabled;
    private readonly string startupValueName;

    public SettingsPlatformActions(
        PreviewDataPaths paths,
        PreviewPersistence persistence,
        bool canConfigureStartup,
        TokenUsageSyncController tokenSync,
        Func<bool> tokenSyncEnabled,
        string startupValueName)
    {
        this.paths = paths;
        this.persistence = persistence;
        this.tokenSync = tokenSync;
        this.tokenSyncEnabled = tokenSyncEnabled;
        this.startupValueName = startupValueName;
        CanConfigureStartup = canConfigureStartup;
    }

    public bool CanConfigureStartup { get; }

    public string TokenSyncStatusText => tokenSync.StatusText;

    public string TokenSyncAddressText => tokenSync.AddressText;

    public string TokenSyncDeviceNameText => tokenSync.DeviceNameText;

    public string TokenSyncMobileStatusText => tokenSync.MobileStatusText;

    public string? TokenSyncPairingInfo => tokenSync.PairingInfo;

    public event EventHandler? TokenSyncChanged
    {
        add => tokenSync.Changed += value;
        remove => tokenSync.Changed -= value;
    }

    public Task SetStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanConfigureStartup)
        {
            throw new InvalidOperationException("预览模式不可配置开机启动。");
        }

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项注册表项。");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
            key.SetValue(startupValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(startupValueName, throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    public void OpenDataDirectory()
    {
        Directory.CreateDirectory(paths.Root);
        _ = Process.Start(new ProcessStartInfo(paths.Root) { UseShellExecute = true });
    }

    public async Task ClearQuotaCacheAsync()
    {
        await persistence.ClearQuotaCacheAsync(QuotaDataSource.CodexCli).ConfigureAwait(false);
        await persistence.ClearQuotaCacheAsync(QuotaDataSource.OAuth).ConfigureAwait(false);
    }

    public Task ApplyTokenSyncEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        tokenSync.SetEnabledAsync(enabled, cancellationToken);

    public void CopyTokenSyncPairingInfo()
    {
        var value = tokenSync.PairingInfo ?? throw new InvalidOperationException("与手机同步当前未监听。");
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public Task RegenerateTokenSyncSecretAsync(CancellationToken cancellationToken) =>
        tokenSync.RegenerateAsync(tokenSyncEnabled(), cancellationToken);
}
