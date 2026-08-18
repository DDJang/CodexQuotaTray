using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenUsageCacheSettingsStateTests
{
    [TestMethod]
    public async Task StartupDisabledThenEnabledPersistsOnNextRefreshWithoutRestart()
    {
        var state = await TokenUsageCacheSettingsState.CreateAsync(Task.FromResult(
            AppSettings.Defaults with { PersistTokenUsageCache = false }));
        var runtime = new StubRuntimeControl(AppSettings.Defaults with { PersistTokenUsageCache = false });
        var control = new TokenUsageCacheRuntimeControl(runtime, Task.FromResult(state));
        var saveCount = 0;

        await control.ApplySettingsAsync(
            runtime.Settings with { PersistTokenUsageCache = true },
            CancellationToken.None);
        var saved = await state.PersistIfEnabledAsync(
            _ =>
            {
                saveCount++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.IsTrue(saved);
        Assert.AreEqual(1, saveCount);
        Assert.IsTrue(state.PersistEnabled);
    }

    [TestMethod]
    public async Task StartupEnabledThenDisabledClearsAndDoesNotRewriteWithoutRestart()
    {
        var cacheExists = true;
        var saveCount = 0;
        var clearCount = 0;
        var state = await TokenUsageCacheSettingsState.CreateAsync(Task.FromResult(
            AppSettings.Defaults with { PersistTokenUsageCache = true }));
        var runtime = new StubRuntimeControl(
            AppSettings.Defaults with { PersistTokenUsageCache = true },
            settings =>
            {
                if (!settings.PersistTokenUsageCache)
                {
                    cacheExists = false;
                    clearCount++;
                }
            });
        var control = new TokenUsageCacheRuntimeControl(runtime, Task.FromResult(state));

        await control.ApplySettingsAsync(
            runtime.Settings with { PersistTokenUsageCache = false },
            CancellationToken.None);
        var saved = await state.PersistIfEnabledAsync(
            _ =>
            {
                cacheExists = true;
                saveCount++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(1, clearCount);
        Assert.IsFalse(saved);
        Assert.AreEqual(0, saveCount);
        Assert.IsFalse(cacheExists);
        Assert.IsFalse(state.PersistEnabled);
    }

    [TestMethod]
    public async Task StartupStateWaitsForLoadedSettingsInsteadOfUsingRuntimeDefaults()
    {
        var settingsCompletion = new TaskCompletionSource<AppSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stateTask = TokenUsageCacheSettingsState.CreateAsync(settingsCompletion.Task);

        Assert.IsFalse(stateTask.IsCompleted);
        settingsCompletion.SetResult(AppSettings.Defaults with { PersistTokenUsageCache = false });
        var state = await stateTask;

        Assert.IsFalse(state.PersistEnabled);
    }

    private sealed class StubRuntimeControl(
        AppSettings initialSettings,
        Action<AppSettings>? applied = null) : IQuotaRuntimeControl
    {
        public AppSettings Settings { get; private set; } = initialSettings;

        public event EventHandler<AppUiState>? StateChanged
        {
            add { }
            remove { }
        }

        public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            applied?.Invoke(settings);
            Settings = settings;
            return Task.CompletedTask;
        }

        public ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
