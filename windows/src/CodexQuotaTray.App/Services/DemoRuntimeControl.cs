using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.App.Services;

/// <summary>
/// Supplies the settings page with an in-memory runtime while the app is running in demo mode.
/// Demo quota data remains owned by <see cref="DemoStateProvider"/>.
/// </summary>
internal sealed class DemoRuntimeControl : IQuotaRuntimeControl
{
    public AppSettings Settings { get; private set; } = AppSettings.Defaults;

    public event EventHandler<AppUiState>? StateChanged
    {
        add { }
        remove { }
    }

    public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Settings = settings with { Notifications = settings.EffectiveNotifications };
        return Task.CompletedTask;
    }

    public ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
