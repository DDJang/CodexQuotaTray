using CodexQuotaTray.App.Services;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class NotificationDeliveryDiagnosticsTests
{
    [TestMethod]
    public void SuccessfulAppDeliveryProducesOneCoherentSnapshot()
    {
        var diagnostics = new NotificationDeliveryDiagnostics();
        var attempt = diagnostics.BeginDelivery();

        diagnostics.RecordAppNotificationFailure(attempt, "app-error");
        diagnostics.RecordAppNotificationSuccess(attempt);

        var snapshot = diagnostics.Snapshot;
        Assert.AreEqual(NotificationDeliveryChannel.AppNotification, snapshot.LastDelivery);
        Assert.AreEqual("none", snapshot.AppNotificationError);
        Assert.AreEqual("none", snapshot.ShellFallbackError);
    }

    [TestMethod]
    public void AppFailureFollowedByShellSuccessPreservesFallbackEvidence()
    {
        var diagnostics = new NotificationDeliveryDiagnostics();
        var attempt = diagnostics.BeginDelivery();

        diagnostics.RecordAppNotificationFailure(attempt, "app-error");
        diagnostics.RecordShellFallbackSuccess(attempt);

        var snapshot = diagnostics.Snapshot;
        Assert.AreEqual(NotificationDeliveryChannel.ShellFallback, snapshot.LastDelivery);
        Assert.AreEqual("app-error", snapshot.AppNotificationError);
        Assert.AreEqual("none", snapshot.ShellFallbackError);
    }

    [TestMethod]
    public void DisabledSettingProducesSuppressedSnapshotWithoutShellEvidence()
    {
        var diagnostics = new NotificationDeliveryDiagnostics();
        var attempt = diagnostics.BeginDelivery();

        diagnostics.RecordSuppressedBySetting(attempt);

        var snapshot = diagnostics.Snapshot;
        Assert.AreEqual(NotificationDeliveryChannel.SuppressedBySetting, snapshot.LastDelivery);
        Assert.AreEqual("none", snapshot.AppNotificationError);
        Assert.AreEqual("none", snapshot.ShellFallbackError);
    }

    [TestMethod]
    public void ShellFailureProducesFailedSnapshot()
    {
        var diagnostics = new NotificationDeliveryDiagnostics();
        var attempt = diagnostics.BeginDelivery();

        diagnostics.RecordAppNotificationFailure(attempt, "app-error");
        diagnostics.RecordShellFallbackFailure(attempt, "shell-error");

        var snapshot = diagnostics.Snapshot;
        Assert.AreEqual(NotificationDeliveryChannel.Failed, snapshot.LastDelivery);
        Assert.AreEqual("app-error", snapshot.AppNotificationError);
        Assert.AreEqual("shell-error", snapshot.ShellFallbackError);
    }

    [TestMethod]
    public void StaleAttemptCannotOverwriteTheLatestAtomicSnapshot()
    {
        var diagnostics = new NotificationDeliveryDiagnostics();
        var staleAttempt = diagnostics.BeginDelivery();
        var currentAttempt = diagnostics.BeginDelivery();

        diagnostics.RecordAppNotificationFailure(staleAttempt, "stale-app-error");
        diagnostics.RecordShellFallbackSuccess(staleAttempt);
        diagnostics.RecordAppNotificationSuccess(currentAttempt);

        var snapshot = diagnostics.Snapshot;
        Assert.AreEqual(currentAttempt.Id, snapshot.AttemptId);
        Assert.AreEqual(NotificationDeliveryChannel.AppNotification, snapshot.LastDelivery);
        Assert.AreEqual("none", snapshot.AppNotificationError);
        Assert.AreEqual("none", snapshot.ShellFallbackError);
    }
}
