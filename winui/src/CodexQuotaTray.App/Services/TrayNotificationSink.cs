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
                try
                {
                    Tray?.ShowQuotaAlert(alert);
                    completion.TrySetResult();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("UI dispatcher is unavailable."));
        }

        return completion.Task;
    }
}
