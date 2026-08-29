using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Runtime;
using Microsoft.UI.Dispatching;

namespace CodexQuotaTray.App.Services;

internal sealed class TrayNotificationSink(
    DispatcherQueue dispatcher,
    WindowsAppNotificationService appNotifications) : IQuotaNotificationSink
{
    internal TrayIconService? Tray { get; set; }

    public Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                _ = DeliverAsync(alert, cancellationToken).ContinueWith(
                    task => CompleteDelivery(task, completion),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }))
        {
            completion.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private async Task DeliverAsync(
        QuotaAlert alert,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        appNotifications.BeginDelivery();
        await NotificationDeliveryRouter.DeliverAsync(
            appNotifications.IsRegistered,
            () => appNotifications.ShowQuotaAlert(alert),
            cancellationToken => ShowShellFallbackAsync(alert, cancellationToken),
            appNotifications.RecordAppNotificationDeliverySuccess,
            appNotifications.RecordAppNotificationDeliveryFailure,
            appNotifications.RecordShellFallbackDeliverySuccess,
            appNotifications.RecordShellFallbackDeliveryFailure,
            cancellationToken).ConfigureAwait(false);
    }

    private static void CompleteDelivery(Task delivery, TaskCompletionSource completion)
    {
        if (delivery.IsCanceled)
        {
            completion.TrySetCanceled();
            return;
        }

        if (delivery.IsFaulted)
        {
            completion.TrySetException(delivery.Exception!.InnerExceptions);
            return;
        }

        completion.TrySetResult();
    }

    private Task ShowShellFallbackAsync(QuotaAlert alert, CancellationToken cancellationToken)
    {
        var tray = Tray
            ?? throw new InvalidOperationException("The tray notification service is unavailable.");
        return tray.ShowQuotaAlertAsync(alert, cancellationToken);
    }
}
