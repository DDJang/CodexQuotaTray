namespace CodexQuotaTray.App.Services;

internal enum TrayBalloonCallback
{
    Show,
    Hide,
    Timeout,
}

internal enum TrayBalloonCallbackDisposition
{
    Ignored,
    Acknowledged,
    Quarantined,
    Drained,
}

internal sealed class TrayBalloonAttemptGate
{
    internal sealed class Attempt
    {
        internal Attempt(long id)
        {
            Id = id;
        }

        internal long Id { get; }

        internal TaskCompletionSource ShowCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DrainCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal AttemptPhase Phase { get; set; } = AttemptPhase.WaitingForShow;
    }

    internal enum AttemptPhase
    {
        WaitingForShow,
        Shown,
        Draining,
    }

    private readonly object gate = new();
    private Attempt? current;
    private long nextId;

    internal bool HasCurrent
    {
        get
        {
            lock (gate)
            {
                return current is not null;
            }
        }
    }

    internal Attempt Begin()
    {
        lock (gate)
        {
            if (current is not null)
            {
                throw new InvalidOperationException("A tray balloon attempt is already active.");
            }

            current = new Attempt(++nextId);
            return current;
        }
    }

    internal void BeginDrain(Attempt attempt)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, attempt)
                || attempt.Phase == AttemptPhase.Shown)
            {
                return;
            }

            attempt.Phase = AttemptPhase.Draining;
        }
    }

    internal TrayBalloonCallbackDisposition Handle(TrayBalloonCallback callback)
    {
        lock (gate)
        {
            if (current is not { } attempt)
            {
                return TrayBalloonCallbackDisposition.Ignored;
            }

            if (attempt.Phase == AttemptPhase.Draining)
            {
                if (callback == TrayBalloonCallback.Show)
                {
                    // This is a late SHOW for the failed attempt. It cannot
                    // acknowledge a later attempt because that attempt is not
                    // admitted until the drain completes.
                    return TrayBalloonCallbackDisposition.Quarantined;
                }

                attempt.DrainCompletion.TrySetResult();
                return TrayBalloonCallbackDisposition.Drained;
            }

            if (attempt.Phase == AttemptPhase.Shown)
            {
                return TrayBalloonCallbackDisposition.Ignored;
            }

            if (callback == TrayBalloonCallback.Show)
            {
                attempt.Phase = AttemptPhase.Shown;
                attempt.ShowCompletion.TrySetResult();
                return TrayBalloonCallbackDisposition.Acknowledged;
            }

            // HIDE/TIMEOUT has no attempt ID. Do not fail the active attempt
            // immediately: it may be a terminal callback from an older
            // balloon. The bounded SHOW wait remains the delivery decision.
            return TrayBalloonCallbackDisposition.Ignored;
        }
    }

    internal void FailCurrent(Exception error)
    {
        lock (gate)
        {
            if (current is not { } attempt)
            {
                return;
            }

            if (attempt.Phase == AttemptPhase.WaitingForShow)
            {
                attempt.Phase = AttemptPhase.Draining;
                attempt.ShowCompletion.TrySetException(error);
            }
        }
    }

    internal bool IsCurrent(Attempt attempt)
    {
        lock (gate)
        {
            return ReferenceEquals(current, attempt);
        }
    }

    internal void End(Attempt attempt)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, attempt))
            {
                current = null;
            }
        }
    }
}
