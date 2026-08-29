using System.Diagnostics;

namespace CodexQuotaTray.App.Services;

internal enum AppNotificationAttemptResult
{
    Delivered,
    SuppressedBySetting,
}

internal static class NotificationDeliveryRouter
{
    internal static async Task DeliverAsync(
        bool appNotificationAvailable,
        Func<AppNotificationAttemptResult> showAppNotification,
        Func<CancellationToken, Task> showShellFallback,
        Action? recordAppNotificationSuccess,
        Action<Exception>? recordAppNotificationFailure,
        Action? recordSuppressedBySetting,
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
                var result = showAppNotification();
                switch (result)
                {
                    case AppNotificationAttemptResult.Delivered:
                        recordAppNotificationSuccess?.Invoke();
                        return;
                    case AppNotificationAttemptResult.SuppressedBySetting:
                        recordSuppressedBySetting?.Invoke();
                        return;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown app notification attempt result: {result}.");
                }
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
