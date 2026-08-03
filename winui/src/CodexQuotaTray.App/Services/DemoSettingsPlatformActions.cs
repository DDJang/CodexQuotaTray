using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.App.Services;

/// <summary>
/// Keeps demo mode side-effect free while allowing every settings command to remain bound.
/// </summary>
internal sealed class DemoSettingsPlatformActions : ISettingsPlatformActions
{
    public Task SetStartupAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
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
}
