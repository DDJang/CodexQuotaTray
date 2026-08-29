using System.Xml.Linq;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Alerts;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class AppIntegrationSourceTests
{
    [TestMethod]
    public void TrayNotificationSinkRoutesAppFailureToShellFallback()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayNotificationSink.cs"));

        StringAssert.Contains(source, "NotificationDeliveryRouter.DeliverAsync");
        StringAssert.Contains(source, "appNotifications.ShowQuotaAlert(alert)");
        StringAssert.Contains(source, "appNotifications.RecordAppNotificationDeliveryFailure");
        StringAssert.Contains(source, "appNotifications.RecordShellFallbackDeliverySuccess");
        StringAssert.Contains(source, "tray.ShowQuotaAlertAsync(alert, cancellationToken)");
        StringAssert.Contains(source, "ContinueWith");
        StringAssert.Contains(source, "completion.TrySetCanceled()");
        Assert.IsFalse(source.Contains("catch (Exception", StringComparison.Ordinal));
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
        StringAssert.Contains(serviceSource, "setting != AppNotificationSetting.Enabled");
        StringAssert.Contains(serviceSource, "new AppNotificationBuilder()");
        StringAssert.Contains(serviceSource, "current.Show(notification)");
        StringAssert.Contains(serviceSource, "current.Unregister()");
    }

    [TestMethod]
    public void WindowsNotificationsUseProductBrandingWithoutRenamingTheExecutable()
    {
        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        var projectSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "App", "CodexQuotaTray.App.csproj"));
        var serviceSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "WindowsAppNotificationService.cs"));

        StringAssert.Contains(projectSource, "<AssemblyName>codex-quota-tray-gui</AssemblyName>");
        StringAssert.Contains(projectSource, "<AssemblyTitle>CodexQuotaTray</AssemblyTitle>");
        StringAssert.Contains(projectSource, "<Product>CodexQuotaTray</Product>");
        StringAssert.Contains(projectSource, "<Description>CodexQuotaTray</Description>");
        StringAssert.Contains(appSource, "appNotifications.TryRegister(identity.DisplayName, iconUri)");
        StringAssert.Contains(appSource, "WindowIconService.AppNotificationIconPath");
        Assert.IsFalse(serviceSource.Contains("codex-quota-tray-gui", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsAppSdkPackageVersionsStayAligned()
    {
        var packageVersions = XDocument.Load(
                Path.Combine(AppContext.BaseDirectory, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(element => element.Attribute("Include") is not null)
            .ToDictionary(
                element => element.Attribute("Include")!.Value,
                element => element.Attribute("Version")?.Value);

        Assert.AreEqual("2.3.1", packageVersions["Microsoft.WindowsAppSDK"]);
        if (packageVersions.TryGetValue("Microsoft.WindowsAppSDK.Runtime", out var runtimeVersion))
        {
            Assert.AreEqual(packageVersions["Microsoft.WindowsAppSDK"], runtimeVersion);
        }
    }

    [TestMethod]
    public void WindowsNotificationRegistrationAndDiagnosticsDistinguishSupportAndFailure()
    {
        var serviceSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "WindowsAppNotificationService.cs"));

        foreach (var state in new[] { "NotAttempted", "Unsupported", "Registered", "Failed" })
        {
            StringAssert.Contains(serviceSource, state);
        }

        StringAssert.Contains(serviceSource, "AppNotificationSupported = AppNotificationManager.IsSupported()");
        StringAssert.Contains(serviceSource, "RegistrationState = AppNotificationRegistrationState.Unsupported");
        StringAssert.Contains(serviceSource, "RegistrationState = AppNotificationRegistrationState.Registered");
        StringAssert.Contains(serviceSource, "RegistrationState = AppNotificationRegistrationState.Failed");
        StringAssert.Contains(serviceSource, "registrationHResult:");
        StringAssert.Contains(serviceSource, "appNotificationRuntimeResourcePresent:");
        StringAssert.Contains(serviceSource, "lastDeliveryChannel:");
        StringAssert.Contains(serviceSource, "lastAppNotificationDeliveryError:");
        StringAssert.Contains(serviceSource, "lastShellFallbackError:");
        StringAssert.Contains(serviceSource, "Microsoft.WindowsAppRuntime.Insights.Resource.dll");
        StringAssert.Contains(serviceSource, "GetType().Name");
        StringAssert.Contains(serviceSource, "FormatDeliveryError");
        StringAssert.Contains(serviceSource, "Windows notifications:");
        StringAssert.Contains(serviceSource, "mode:");
        StringAssert.Contains(serviceSource, ".AddText(title)");
        StringAssert.Contains(serviceSource, ".AddText(body)");
        Assert.IsFalse(serviceSource.Contains("AddImage", StringComparison.Ordinal));
        Assert.IsFalse(serviceSource.Contains("SetAppLogoOverride", StringComparison.Ordinal));
        Assert.IsFalse(serviceSource.Contains("SetHeroImage", StringComparison.Ordinal));
        Assert.IsFalse(serviceSource.Contains("SetInlineImage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowsUpdateNotificationsUseTheSameFallbackRouter()
    {
        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));
        var methodStart = appSource.IndexOf("private async Task DeliverWindowsUpdateNotificationAsync(", StringComparison.Ordinal);
        var methodEnd = appSource.IndexOf(
            "private async Task StartWindowsUpdateCheckAfterInitializationAsync(",
            methodStart,
            StringComparison.Ordinal);

        Assert.IsTrue(methodStart >= 0);
        Assert.IsTrue(methodEnd > methodStart);
        var method = appSource[methodStart..methodEnd];
        StringAssert.Contains(method, "NotificationDeliveryRouter.DeliverAsync");
        StringAssert.Contains(method, "ShowWindowsUpdateAvailable(release)");
        StringAssert.Contains(method, "RecordAppNotificationDeliveryFailure");
        StringAssert.Contains(method, "RecordShellFallbackDeliverySuccess");
        StringAssert.Contains(method, "CancellationToken.None");
        StringAssert.Contains(appSource, "ObserveWindowsUpdateDelivery");
    }

    [TestMethod]
    public void ShellFallbackUsesNoBodyIconAndNoRealtimeFlags()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayIconService.cs"));
        var nativeSource = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Interop", "NativeMethods.cs"));

        StringAssert.Contains(source, "WindowIconService.TrayIconPath");
        StringAssert.Contains(source, "BalloonIcon = IntPtr.Zero");
        StringAssert.Contains(source, "data.Flags |= NativeMethods.NifInfo;");
        StringAssert.Contains(source, "data.InfoFlags = NativeMethods.NiifNone;");
        StringAssert.Contains(nativeSource, "NiifNone = 0x00000000");
        Assert.IsFalse(source.Contains("NativeMethods.NiifInfo", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("NativeMethods.NiifUser", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("NativeMethods.NiifLargeIcon", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("NativeMethods.NifRealtime", StringComparison.Ordinal));
        Assert.IsFalse(nativeSource.Contains("NiifUser", StringComparison.Ordinal));
        Assert.IsFalse(nativeSource.Contains("NiifLargeIcon", StringComparison.Ordinal));
        Assert.IsFalse(nativeSource.Contains("NifRealtime", StringComparison.Ordinal));
        StringAssert.Contains(nativeSource, "internal IntPtr BalloonIcon;");
    }

    [TestMethod]
    public void TrayStartupRequiresOnlyTheSmallIcon()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayIconService.cs"));

        StringAssert.Contains(source, "var smallIcons = new IntPtr[1];");
        StringAssert.Contains(
            source,
            "NativeMethods.ExtractIconEx(iconPath, 0, null, smallIcons, 1)");
        StringAssert.Contains(source, "smallIconExtractResult != 1");
        StringAssert.Contains(source, "smallIconHandlePresent != true");
        StringAssert.Contains(source, "LogIconExtraction(\"tray icon loaded\")");
        StringAssert.Contains(source, "托盘小图标提取结果:");
        Assert.IsFalse(source.Contains("balloonIcon", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("largeIcons", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("TryLoadLargeBalloonIcon", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("气泡大图标", StringComparison.Ordinal));
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
        Assert.IsFalse(source.Contains("NativeMethods.NifRealtime", StringComparison.Ordinal));
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
