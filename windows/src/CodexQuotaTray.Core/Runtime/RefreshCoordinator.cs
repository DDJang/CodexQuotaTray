namespace CodexQuotaTray.Core.Runtime;

public enum RefreshMode
{
    Auto,
    Every5Minutes,
    Every15Minutes,
    Every30Minutes,
    ManualOnly,
}

public enum RefreshReason
{
    Startup,
    Scheduled,
    RateLimitNotification,
    NetworkRestored,
    Resume,
    CardOpened,
    Manual,
}

public enum RefreshDecision
{
    Start,
    Queue,
    Suppress,
}

public sealed class RefreshCoordinator
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];

    private readonly object gate = new();
    private RefreshReason? pending;
    private bool inFlight;

    public RefreshMode Mode { get; private set; } = RefreshMode.Every15Minutes;

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? LastSuccessUtc { get; private set; }

    public RefreshReason? PendingReason
    {
        get
        {
            lock (gate)
            {
                return pending;
            }
        }
    }

    public bool IsInFlight
    {
        get
        {
            lock (gate)
            {
                return inFlight;
            }
        }
    }

    public void SetMode(RefreshMode mode)
    {
        lock (gate)
        {
            Mode = mode == RefreshMode.Auto ? RefreshMode.Every15Minutes : mode;
        }
    }

    public void RestoreLastSuccess(DateTimeOffset value)
    {
        lock (gate)
        {
            LastSuccessUtc = value;
        }
    }

    public RefreshDecision Request(RefreshReason reason)
    {
        lock (gate)
        {
            if (!inFlight)
            {
                inFlight = true;
                return RefreshDecision.Start;
            }

            if (pending is null || Priority(reason) > Priority(pending.Value))
            {
                pending = reason;
            }

            return RefreshDecision.Queue;
        }
    }

    public RefreshReason? Complete(bool succeeded, DateTimeOffset nowUtc)
        => CompleteAndHandoff(succeeded, nowUtc);

    public RefreshReason? CompleteAndHandoff(bool succeeded, DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (succeeded)
            {
                ConsecutiveFailures = 0;
                LastSuccessUtc = nowUtc;
            }
            else
            {
                ConsecutiveFailures++;
            }

            var next = pending;
            pending = null;
            if (next is not null)
            {
                return next;
            }

            inFlight = false;
            return null;
        }
    }

    public RefreshReason? AbandonCurrentAndContinue()
    {
        lock (gate)
        {
            var next = pending;
            pending = null;
            if (next is not null)
            {
                return next;
            }

            inFlight = false;
            return null;
        }
    }

    public void Release()
    {
        lock (gate)
        {
            inFlight = false;
            pending = null;
        }
    }

    public TimeSpan EffectiveInterval(int? minimumReliableRemaining)
    {
        var baseline = Mode switch
        {
            RefreshMode.ManualOnly => TimeSpan.FromMinutes(60),
            RefreshMode.Every5Minutes => TimeSpan.FromMinutes(5),
            RefreshMode.Every15Minutes => TimeSpan.FromMinutes(15),
            RefreshMode.Every30Minutes => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromMinutes(15),
        };

        if (Mode == RefreshMode.ManualOnly || ConsecutiveFailures == 0)
        {
            return baseline;
        }

        var backoff = Backoff[Math.Min(ConsecutiveFailures - 1, Backoff.Length - 1)];
        return Max(baseline, backoff);
    }

    public TimeSpan StaleAfter(int? minimumReliableRemaining) =>
        Mode == RefreshMode.ManualOnly
            ? TimeSpan.FromMinutes(60)
            : Max(TimeSpan.FromMinutes(15), EffectiveInterval(minimumReliableRemaining) * 2);

    private static int Priority(RefreshReason reason) => reason switch
    {
        RefreshReason.Manual => 100,
        RefreshReason.Resume or RefreshReason.NetworkRestored => 80,
        RefreshReason.CardOpened => 60,
        RefreshReason.RateLimitNotification => 40,
        RefreshReason.Startup => 20,
        _ => 10,
    };

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}
