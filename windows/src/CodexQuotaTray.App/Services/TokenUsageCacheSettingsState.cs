using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.App.Services;

internal sealed class TokenUsageCacheSettingsState(bool initialPersistEnabled)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool persistEnabled = initialPersistEnabled;

    internal bool PersistEnabled => Volatile.Read(ref persistEnabled);

    internal static async Task<TokenUsageCacheSettingsState> CreateAsync(Task<AppSettings> settingsTask)
    {
        var settings = await settingsTask.ConfigureAwait(false);
        return new TokenUsageCacheSettingsState(settings.PersistTokenUsageCache);
    }

    internal async Task ApplySettingsAsync(
        Func<CancellationToken, Task<AppSettings>> applySettings,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await applySettings(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref persistEnabled, settings.PersistTokenUsageCache);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<bool> PersistIfEnabledAsync(
        Func<CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!persistEnabled)
            {
                return false;
            }

            await persist(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }
}

internal sealed class TokenUsageCacheRuntimeControl(
    IQuotaRuntimeControl inner,
    Task<TokenUsageCacheSettingsState> settingsStateTask) : IQuotaRuntimeControl
{
    public AppSettings Settings => inner.Settings;

    public event EventHandler<AppUiState>? StateChanged
    {
        add => inner.StateChanged += value;
        remove => inner.StateChanged -= value;
    }

    public event EventHandler? TokenRefreshScheduleChanged
    {
        add => inner.TokenRefreshScheduleChanged += value;
        remove => inner.TokenRefreshScheduleChanged -= value;
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var settingsState = await settingsStateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        await settingsState.ApplySettingsAsync(
            async token =>
            {
                await inner.ApplySettingsAsync(settings, token).ConfigureAwait(false);
                return inner.Settings;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default) =>
        inner.RequestAsync(reason, cancellationToken);
}
