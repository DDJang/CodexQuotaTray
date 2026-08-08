using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.App.Services;

/// <summary>
/// Keeps demo mode side-effect free while allowing every settings command to remain bound.
/// </summary>
internal sealed class DemoSettingsPlatformActions : ISettingsPlatformActions
{
    public bool CanConfigureStartup => false;

    public Task SetStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("预览模式不可配置开机启动。");
    }

    public void OpenDataDirectory()
    {
    }

    public Task<int> ImportProductionDataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public Task ClearQuotaCacheAsync() => Task.CompletedTask;

    public string TokenSyncStatusText => "预览模式不可用";

    public string TokenSyncAddressText => string.Empty;

    public Task ApplyTokenSyncEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

    public void CopyTokenSyncPairingInfo() => throw new InvalidOperationException("预览模式不可用。");

    public Task RegenerateTokenSyncSecretAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
