using CodexQuotaTray.App.Services;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class NotificationDeliveryRouterTests
{
    [TestMethod]
    public async Task UnregisteredAppNotificationUsesShellFallback()
    {
        var calls = new List<string>();

        await NotificationDeliveryRouter.DeliverAsync(
            appNotificationAvailable: false,
            showAppNotification: () => calls.Add("app"),
            showShellFallback: _ =>
            {
                calls.Add("shell");
                return Task.CompletedTask;
            },
            recordAppNotificationSuccess: () => calls.Add("app-success"),
            recordAppNotificationFailure: _ => calls.Add("app-failure"),
            recordShellFallbackSuccess: () => calls.Add("shell-success"),
            recordShellFallbackFailure: _ => calls.Add("shell-failure"),
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "shell", "shell-success" }, calls);
    }

    [TestMethod]
    public async Task AppNotificationSuccessDoesNotUseShellFallback()
    {
        var calls = new List<string>();

        await NotificationDeliveryRouter.DeliverAsync(
            appNotificationAvailable: true,
            showAppNotification: () => calls.Add("app"),
            showShellFallback: _ =>
            {
                calls.Add("shell");
                return Task.CompletedTask;
            },
            recordAppNotificationSuccess: () => calls.Add("app-success"),
            recordAppNotificationFailure: _ => calls.Add("app-failure"),
            recordShellFallbackSuccess: () => calls.Add("shell-success"),
            recordShellFallbackFailure: _ => calls.Add("shell-failure"),
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "app", "app-success" }, calls);
    }

    [TestMethod]
    public async Task RecoverableAppNotificationFailureUsesShellFallback()
    {
        var appError = new InvalidOperationException("app delivery failed");
        Exception? recordedAppError = null;
        var calls = new List<string>();

        await NotificationDeliveryRouter.DeliverAsync(
            appNotificationAvailable: true,
            showAppNotification: () => throw appError,
            showShellFallback: _ =>
            {
                calls.Add("shell");
                return Task.CompletedTask;
            },
            recordAppNotificationSuccess: () => calls.Add("app-success"),
            recordAppNotificationFailure: error =>
            {
                calls.Add("app-failure");
                recordedAppError = error;
            },
            recordShellFallbackSuccess: () => calls.Add("shell-success"),
            recordShellFallbackFailure: _ => calls.Add("shell-failure"),
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "app-failure", "shell", "shell-success" }, calls);
        Assert.AreSame(appError, recordedAppError);
    }

    [TestMethod]
    public async Task BothChannelsFailReturnsShellFailure()
    {
        var appError = new InvalidOperationException("app delivery failed");
        var shellError = new IOException("shell delivery failed");
        Exception? recordedAppError = null;
        Exception? recordedShellError = null;
        var calls = new List<string>();

        var thrown = await Assert.ThrowsExactlyAsync<IOException>(() =>
            NotificationDeliveryRouter.DeliverAsync(
                appNotificationAvailable: true,
                showAppNotification: () => throw appError,
                showShellFallback: _ =>
                {
                    calls.Add("shell");
                    return Task.FromException(shellError);
                },
                recordAppNotificationSuccess: () => calls.Add("app-success"),
                recordAppNotificationFailure: error =>
                {
                    calls.Add("app-failure");
                    recordedAppError = error;
                },
                recordShellFallbackSuccess: () => calls.Add("shell-success"),
                recordShellFallbackFailure: error =>
                {
                    calls.Add("shell-failure");
                    recordedShellError = error;
                },
                CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "app-failure", "shell", "shell-failure" }, calls);
        Assert.AreSame(appError, recordedAppError);
        Assert.AreSame(shellError, recordedShellError);
        Assert.AreSame(shellError, thrown);
    }

    [TestMethod]
    public async Task CancellationBeforeDeliveryDoesNotInvokeEitherChannel()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var appCalled = false;
        var shellCalled = false;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            NotificationDeliveryRouter.DeliverAsync(
                appNotificationAvailable: true,
                showAppNotification: () => appCalled = true,
                showShellFallback: _ =>
                {
                    shellCalled = true;
                    return Task.CompletedTask;
                },
                recordAppNotificationSuccess: null,
                recordAppNotificationFailure: null,
                recordShellFallbackSuccess: null,
                recordShellFallbackFailure: null,
                cancellation.Token));

        Assert.IsFalse(appCalled);
        Assert.IsFalse(shellCalled);
    }

    [TestMethod]
    public async Task AppNotificationCancellationDoesNotUseShellFallback()
    {
        var appError = new OperationCanceledException();
        var shellCalled = false;

        var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            NotificationDeliveryRouter.DeliverAsync(
                appNotificationAvailable: true,
                showAppNotification: () => throw appError,
                showShellFallback: _ =>
                {
                    shellCalled = true;
                    return Task.CompletedTask;
                },
                recordAppNotificationSuccess: null,
                recordAppNotificationFailure: null,
                recordShellFallbackSuccess: null,
                recordShellFallbackFailure: null,
                CancellationToken.None));

        Assert.AreSame(appError, thrown);
        Assert.IsFalse(shellCalled);
    }

    [TestMethod]
    public async Task FatalAppNotificationFailureDoesNotUseShellFallback()
    {
        var shellCalled = false;

        await Assert.ThrowsExactlyAsync<OutOfMemoryException>(() =>
            NotificationDeliveryRouter.DeliverAsync(
                appNotificationAvailable: true,
                showAppNotification: () => throw new OutOfMemoryException(),
                showShellFallback: _ =>
                {
                    shellCalled = true;
                    return Task.CompletedTask;
                },
                recordAppNotificationSuccess: null,
                recordAppNotificationFailure: null,
                recordShellFallbackSuccess: null,
                recordShellFallbackFailure: null,
                CancellationToken.None));

        Assert.IsFalse(shellCalled);
    }
}
