namespace CodexQuotaTray.App.Services;

internal enum NotificationDeliveryChannel
{
    None,
    AppNotification,
    ShellFallback,
    SuppressedBySetting,
    Failed,
}

internal readonly record struct NotificationDeliveryAttempt(long Id);

internal sealed record NotificationDeliveryDiagnostic(
    long AttemptId,
    NotificationDeliveryChannel LastDelivery,
    string AppNotificationError,
    string ShellFallbackError)
{
    internal static NotificationDeliveryDiagnostic Empty { get; } = new(
        AttemptId: 0,
        LastDelivery: NotificationDeliveryChannel.None,
        AppNotificationError: "none",
        ShellFallbackError: "none");
}

internal sealed class NotificationDeliveryDiagnostics
{
    private long nextAttemptId;
    private NotificationDeliveryDiagnostic snapshot = NotificationDeliveryDiagnostic.Empty;

    internal NotificationDeliveryAttempt BeginDelivery()
    {
        var attempt = new NotificationDeliveryAttempt(Interlocked.Increment(ref nextAttemptId));
        Interlocked.Exchange(
            ref snapshot,
            new NotificationDeliveryDiagnostic(
                attempt.Id,
                NotificationDeliveryChannel.None,
                "none",
                "none"));
        return attempt;
    }

    internal NotificationDeliveryDiagnostic Snapshot => Volatile.Read(ref snapshot);

    internal void RecordAppNotificationSuccess(NotificationDeliveryAttempt attempt) => Update(
        attempt,
        current => current with
        {
            LastDelivery = NotificationDeliveryChannel.AppNotification,
            AppNotificationError = "none",
            ShellFallbackError = "none",
        });

    internal void RecordAppNotificationFailure(NotificationDeliveryAttempt attempt, string error) => Update(
        attempt,
        current => current with { AppNotificationError = error });

    internal void RecordSuppressedBySetting(NotificationDeliveryAttempt attempt) => Update(
        attempt,
        current => current with
        {
            LastDelivery = NotificationDeliveryChannel.SuppressedBySetting,
            AppNotificationError = "none",
            ShellFallbackError = "none",
        });

    internal void RecordShellFallbackSuccess(NotificationDeliveryAttempt attempt) => Update(
        attempt,
        current => current with
        {
            LastDelivery = NotificationDeliveryChannel.ShellFallback,
            ShellFallbackError = "none",
        });

    internal void RecordShellFallbackFailure(NotificationDeliveryAttempt attempt, string error) => Update(
        attempt,
        current => current with
        {
            LastDelivery = NotificationDeliveryChannel.Failed,
            ShellFallbackError = error,
        });

    private void Update(
        NotificationDeliveryAttempt attempt,
        Func<NotificationDeliveryDiagnostic, NotificationDeliveryDiagnostic> update)
    {
        while (true)
        {
            var current = Volatile.Read(ref snapshot);
            if (current.AttemptId != attempt.Id)
            {
                return;
            }

            var next = update(current);
            var observed = Interlocked.CompareExchange(ref snapshot, next, current);
            if (ReferenceEquals(observed, current))
            {
                return;
            }
        }
    }
}
