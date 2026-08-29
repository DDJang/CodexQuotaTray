using System.Diagnostics;

namespace CodexQuotaTray.App.Services;

internal static class NotificationDeliveryRouter
{
    internal static async Task DeliverAsync(
        bool appNotificationAvailable,
        Action showAppNotification,
        Func<CancellationToken, Task> showShellFallback,
        Action? recordAppNotificationSuccess,
        Action<Exception>? recordAppNotificationFailure,
        Action? recordShellFallbackSuccess,
        Action<Exception>? recordShellFallbackFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showAppNotification);
        ArgumentNullException.ThrowIfNull(showShellFallback);
        cancellationToken.ThrowIfCancellationRequested();

        if (appNotificationAvailable)
        {
            try
            {
                showAppNotification();
                recordAppNotificationSuccess?.Invoke();
                return;
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                recordAppNotificationFailure?.Invoke(error);
                Debug.WriteLine(
                    $"Windows AppNotification delivery failed; trying Shell fallback: {FormatError(error)}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await showShellFallback(cancellationToken).ConfigureAwait(false);
            recordShellFallbackSuccess?.Invoke();
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            recordShellFallbackFailure?.Invoke(error);
            throw;
        }
    }

    private static bool IsRecoverable(Exception error) =>
        error is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException;

    private static string FormatError(Exception error) =>
        $"{error.GetType().Name}(0x{unchecked((uint)error.HResult):X8})";
}
