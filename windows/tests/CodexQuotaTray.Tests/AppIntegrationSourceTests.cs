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
}
