namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class AppIntegrationSourceTests
{
    [TestMethod]
    public void TrayNotificationSinkFailsClosedBeforeTrayIsAvailable()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayNotificationSink.cs"));

        StringAssert.Contains(source, "?? throw new InvalidOperationException");
        StringAssert.Contains(source, "tray.ShowQuotaAlert(alert)");
        Assert.IsFalse(source.Contains("Tray?.ShowQuotaAlert", StringComparison.Ordinal));
    }

    [TestMethod]
    public void QuotaNotificationShellFailureIsReportedToTheRuntime()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "TrayIconService.cs"));

        StringAssert.Contains(source, "if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data))");
        StringAssert.Contains(source, "throw LastWin32(\"show quota notification\")");
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
    public void UiAndLanTokenUsagePathsShareOneScannerInstance()
    {
        var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));

        Assert.AreEqual(1, appSource.Split("new TokenUsageScanner()", StringSplitOptions.None).Length - 1);
        var controllerStart = appSource.IndexOf("new TokenUsageSyncController(", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, controllerStart);
        var controllerEnd = appSource.IndexOf(");", controllerStart, StringComparison.Ordinal);
        Assert.IsGreaterThan(controllerStart, controllerEnd);
        StringAssert.Contains(appSource[controllerStart..controllerEnd], "tokenUsageScanner,");
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
        StringAssert.Contains(source, "CloseApplications=yes");
        Assert.IsFalse(method.Contains("ewWaitUntilTerminated", StringComparison.Ordinal));
    }
}
