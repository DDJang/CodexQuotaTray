using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Runtime;
using Microsoft.UI.Dispatching;

namespace CodexQuotaTray.App.Services;

internal sealed class TrayNotificationSink(DispatcherQueue dispatcher) : IQuotaNotificationSink
{
    internal TrayIconService? Tray { get; set; }

    public Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                _ = DeliverAsync(alert, cancellationToken, completion);
            }))
        {
            completion.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private async Task DeliverAsync(
        QuotaAlert alert,
        CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        try
        {
            var tray = Tray
                ?? throw new InvalidOperationException("The tray notification service is unavailable.");
            await tray.ShowQuotaAlertAsync(alert, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }
}
