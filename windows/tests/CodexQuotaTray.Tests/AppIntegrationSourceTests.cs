using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Alerts;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class AppIntegrationSourceTests
{
    [TestMethod]
    public void TrayNotificationSinkPrefersWindowsNotificationsAndKeepsTrayFallback()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayNotificationSink.cs"));

        StringAssert.Contains(source, "if (appNotifications.IsRegistered)");
        StringAssert.Contains(source, "appNotifications.ShowQuotaAlert(alert)");
        StringAssert.Contains(source, "?? throw new InvalidOperationException");
        StringAssert.Contains(source, "tray.ShowQuotaAlertAsync(alert, cancellationToken)");
        Assert.IsFalse(source.Contains("Tray?.ShowQuotaAlert", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OnlyCurrentMainInstanceRegistersWindowsNotifications()
    {
        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        var serviceSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "WindowsAppNotificationService.cs"));

        var findInstance = appSource.IndexOf("AppInstance.FindOrRegisterForKey", StringComparison.Ordinal);
        var secondaryPath = appSource.IndexOf("if (!currentInstance.IsCurrent)", findInstance, StringComparison.Ordinal);
        var shutdownPath = appSource.IndexOf("if (HasArgument(arguments, \"--shutdown-existing\"))", secondaryPath, StringComparison.Ordinal);
        var registration = appSource.IndexOf("appNotifications.TryRegister", shutdownPath, StringComparison.Ordinal);
        Assert.IsTrue(findInstance >= 0);
        Assert.IsTrue(secondaryPath > findInstance);
        Assert.IsTrue(shutdownPath > secondaryPath);
        Assert.IsTrue(registration > shutdownPath);
        var auxiliaryInstancePaths = appSource[secondaryPath..registration];
        StringAssert.Contains(
            auxiliaryInstancePaths,
            "RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs())");
        Assert.IsFalse(auxiliaryInstancePaths.Contains("TryRegister", StringComparison.Ordinal));
        Assert.IsFalse(auxiliaryInstancePaths.Contains("DisposeAppNotifications", StringComparison.Ordinal));

        StringAssert.Contains(serviceSource, "AppNotificationManager.IsSupported()");
        StringAssert.Contains(serviceSource, "candidate.Register(displayName, iconUri)");
        StringAssert.Contains(serviceSource, "current.Setting != AppNotificationSetting.Enabled");
        StringAssert.Contains(serviceSource, "new AppNotificationBuilder()");
        StringAssert.Contains(serviceSource, "current.Show(notification)");
        StringAssert.Contains(serviceSource, "current.Unregister()");
    }

    [TestMethod]
    public void ResetNotificationFormatterPreservesUnknownRemainingValue()
    {
        var alert = QuotaAlert.ForReset(
        [
            new QuotaResetWindow("5 小时额度", null, null),
        ]);

        var content = QuotaNotificationFormatter.Format(alert);

        Assert.AreEqual("Codex 额度提醒", content.Title);
        StringAssert.Contains(content.Body, "5 小时额度已重置");
        StringAssert.Contains(content.Body, "当前剩余 未知");
        StringAssert.Contains(content.Body, "下次重置时间为 未知");
        Assert.IsFalse(content.Body.Contains("%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QuotaNotificationShellFailureIsReportedToTheRuntime()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayIconService.cs"));

        StringAssert.Contains(source, "if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data))");
        StringAssert.Contains(source, "throw LastWin32(\"show quota notification\")");
        StringAssert.Contains(source, "await attempt.ShowCompletion.Task.WaitAsync");
        StringAssert.Contains(source, "BalloonShowAcknowledgementTimeout");
        StringAssert.Contains(source, "BalloonCallbackDrainTimeout");
        StringAssert.Contains(source, "DrainCompletion.Task.WaitAsync");
        StringAssert.Contains(source, "NativeMethods.NifRealtime");
    }

    [TestMethod]
    public void TrayBalloonLifecycleUsesShowAsTheOnlySuccessAcknowledgement()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayIconService.cs"));
        var nativeSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Interop", "NativeMethods.cs"));

        StringAssert.Contains(nativeSource, "NinBalloonShow = 0x0402");
        StringAssert.Contains(nativeSource, "NinBalloonHide = 0x0403");
        StringAssert.Contains(nativeSource, "NinBalloonTimeout = 0x0404");
        StringAssert.Contains(source, "trayEvent == NativeMethods.NinBalloonShow");
        StringAssert.Contains(source, "NativeMethods.NinBalloonHide or NativeMethods.NinBalloonTimeout");
        StringAssert.Contains(source, "balloonAttemptGate.Handle");
        StringAssert.Contains(source, "balloonAttemptGate.BeginDrain");
        StringAssert.Contains(source, "FailPendingBalloon");

        var drainStart = source.IndexOf("balloonAttemptGate.BeginDrain(attempt)", StringComparison.Ordinal);
        var dismiss = source.IndexOf("DismissPendingBalloon(attempt)", drainStart, StringComparison.Ordinal);
        var drainWait = source.IndexOf("attempt.DrainCompletion.Task.WaitAsync", dismiss, StringComparison.Ordinal);
        var end = source.IndexOf("balloonAttemptGate.End(attempt)", drainWait, StringComparison.Ordinal);
        Assert.IsTrue(drainStart >= 0);
        Assert.IsTrue(dismiss > drainStart);
        Assert.IsTrue(drainWait > dismiss);
        Assert.IsTrue(end > drainWait);
    }

    [TestMethod]
    public void MainWindowIsNotActivatedBeforeStartupVisibilityIsRequested()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));

        Assert.IsFalse(source.Contains("mainWindow.Activate()", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mainWindow?.Activate()", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mainWindow.HidePanel()", StringComparison.Ordinal));
        StringAssert.Contains(source, "else if (startupLaunch)");
        StringAssert.Contains(source, "mainWindow?.ShowPanel()");
    }

    [TestMethod]
    public void ShutdownActivationStartsExitBeforeShowingMainWindow()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        var methodStart = source.IndexOf("private void OnInstanceActivated(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task<TokenUsageSnapshot> ScanTokenUsageAsync(",
            methodStart,
            StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0);
        Assert.IsTrue(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var shutdown = method.IndexOf("ActivationContains(args, \"--shutdown-existing\")", StringComparison.Ordinal);
        var exit = method.IndexOf("ExitApplication();", shutdown, StringComparison.Ordinal);
        var returnAfterExit = method.IndexOf("return;", exit, StringComparison.Ordinal);
        var show = method.IndexOf("mainWindow?.ShowPanel();", StringComparison.Ordinal);

        Assert.IsTrue(shutdown >= 0);
        Assert.IsTrue(exit > shutdown);
        Assert.IsTrue(returnAfterExit > exit);
        Assert.IsTrue(show > returnAfterExit);
    }

    [TestMethod]
    public void ExitLifecycleUsesAnIdempotentGuardAndExplicitApplicationExit()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));

        StringAssert.Contains(source, "private int exitStarted;");
        StringAssert.Contains(source, "SessionEndingPolicy.ExitForWindowsUpdate(crashSessionLog, StartExit);");

        var startExitStart = source.IndexOf("private void StartExit()", StringComparison.Ordinal);
        var completeExitStart = source.IndexOf("private async Task CompleteExitAsync()", startExitStart, StringComparison.Ordinal);
        Assert.IsTrue(startExitStart >= 0);
        Assert.IsTrue(completeExitStart > startExitStart);
        var startExit = source[startExitStart..completeExitStart];

        var guard = startExit.IndexOf("Interlocked.Exchange(ref exitStarted, 1)", StringComparison.Ordinal);
        var threadCheck = startExit.IndexOf("if (dispatcher.HasThreadAccess)", StringComparison.Ordinal);
        var directStart = startExit.IndexOf("_ = CompleteExitAsync();", threadCheck, StringComparison.Ordinal);
        var enqueue = startExit.IndexOf(
            "dispatcher.TryEnqueue(() => _ = CompleteExitAsync())",
            threadCheck,
            StringComparison.Ordinal);

        Assert.IsTrue(guard >= 0);
        Assert.IsTrue(threadCheck > guard);
        Assert.IsTrue(directStart > threadCheck);
        Assert.IsTrue(enqueue > directStart);
        StringAssert.Contains(startExit, "if (dispatcher is null)");
        StringAssert.Contains(startExit, "FallbackExitWithoutUiDispatcher();");
        StringAssert.Contains(startExit, "crashSessionLog?.MarkExpectedTermination();");
        StringAssert.Contains(startExit, "_ = ForceExitAfterGracePeriodAsync();");
        StringAssert.Contains(source, "crashSessionLog?.CompleteSession();");
        StringAssert.Contains(source, "Environment.Exit(0);");

        var deadlineStart = source.IndexOf("private static async Task ForceExitAfterGracePeriodAsync()", StringComparison.Ordinal);
        var fallbackStart = source.IndexOf("private void FallbackExitWithoutUiDispatcher()", deadlineStart, StringComparison.Ordinal);
        Assert.IsTrue(deadlineStart >= 0);
        Assert.IsTrue(fallbackStart > deadlineStart);
        var deadline = source[deadlineStart..fallbackStart];
        StringAssert.Contains(deadline, "await Task.Delay(ExitGracePeriod).ConfigureAwait(false);");
        StringAssert.Contains(deadline, "Environment.Exit(0);");

        var cleanupStart = source.IndexOf("private async Task CompleteExitAsync()", StringComparison.Ordinal);
        var cleanupEnd = source.IndexOf("internal static bool HasArgument(", cleanupStart, StringComparison.Ordinal);
        Assert.IsTrue(cleanupStart >= 0);
        Assert.IsTrue(cleanupEnd > cleanupStart);
        var cleanup = source[cleanupStart..cleanupEnd];

        StringAssert.Contains(cleanup, "await initializationTask.WaitAsync(TimeSpan.FromSeconds(2));");
        StringAssert.Contains(cleanup, "catch (TimeoutException)");
        StringAssert.Contains(cleanup, "Exit();");
    }

    [TestMethod]
    public void MainWindowPreparesItsFirstPresentationBeforeShowing()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml.cs"));
        var methodStart = source.IndexOf("private void ShowPanelCore(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("internal void ApplyTheme(", methodStart, StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0);
        Assert.IsTrue(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var configure = method.IndexOf("ConfigureWindow();", StringComparison.Ordinal);
        var backdrop = method.IndexOf("ApplyBackdrop();", StringComparison.Ordinal);
        var position = method.IndexOf("Position();", StringComparison.Ordinal);
        var activate = method.IndexOf("Activate();", StringComparison.Ordinal);
        var show = method.IndexOf("appWindow.Show();", StringComparison.Ordinal);
        var correction = method.IndexOf("QueuePositionIfVisible(forceResize: true);", StringComparison.Ordinal);

        Assert.IsTrue(configure >= 0);
        Assert.IsTrue(backdrop > configure);
        Assert.IsTrue(position > backdrop);
        Assert.IsTrue(activate > position);
        Assert.IsTrue(show > activate);
        Assert.IsTrue(correction > show);
    }

    [TestMethod]
    public void MainWindowUsesNativeDpiWhenXamlRootIsUnavailable()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml.cs"));

        StringAssert.Contains(source, "WindowPlacementService.GetRasterizationScale(hwnd)");
    }

    [TestMethod]
    public void TokenUsageCacheRestoreStillUsesStartupSettingsWithoutQuotaInitialization()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        var methodStart = source.IndexOf("private async Task InitializeTokenUsageAsync(", StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0);
        var methodEnd = source.IndexOf(
            "private async Task RefreshTokenUsageOnPanelShownAsync(",
            methodStart,
            StringComparison.Ordinal);
        Assert.IsTrue(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        StringAssert.Contains(method, "var settings = await settingsTask;");
        StringAssert.Contains(method, "if (!settings.PersistTokenUsageCache)");
        StringAssert.Contains(method, "LoadTokenUsageCacheAsync(cancellationToken)");
        Assert.IsFalse(method.Contains("initializationTask", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TokenUsageCacheWritesUseCurrentSettingsStateWithoutWriteThenClear()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        var methodStart = source.IndexOf("private async Task<TokenUsageSnapshot> ScanTokenUsageAsync(", StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0);
        var methodEnd = source.IndexOf(
            "private async Task InitializeTokenUsageAsync(",
            methodStart,
            StringComparison.Ordinal);
        Assert.IsTrue(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        StringAssert.Contains(method, "tokenUsageCacheSettingsStateTask.WaitAsync(cancellationToken)");
        StringAssert.Contains(method, "PersistIfEnabledAsync(");
        Assert.IsFalse(method.Contains("tokenUsageSettingsTask", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ClearTokenUsageCacheAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UiAndLanTokenUsagePathsShareOneSourceResolver()
    {
        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));

        Assert.AreEqual(1, appSource.Split("new TokenUsageScanner(paths.TokenUsageDatabase)", StringSplitOptions.None).Length - 1);
        var controllerStart = appSource.IndexOf("new TokenUsageSyncController(", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, controllerStart);
        var controllerEnd = appSource.IndexOf(");", controllerStart, StringComparison.Ordinal);
        Assert.IsGreaterThan(controllerStart, controllerEnd);
        StringAssert.Contains(appSource[controllerStart..controllerEnd], "tokenUsageSourceResolver.ReadAsync(cancellationToken)");
    }

    [TestMethod]
    public void InstallerDoesNotWaitUnboundedForShutdownHelper()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Installer", "CodexQuotaTray.iss"));
        var methodStart = source.IndexOf("function PrepareToInstall(", StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0);
        var methodEnd = source.IndexOf("\nend;", methodStart, StringComparison.Ordinal);
        Assert.IsTrue(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        StringAssert.Contains(method, "ewNoWait");
        StringAssert.Contains(source, "CloseApplications=force");
        Assert.IsFalse(method.Contains("ewWaitUntilTerminated", StringComparison.Ordinal));
    }
}
