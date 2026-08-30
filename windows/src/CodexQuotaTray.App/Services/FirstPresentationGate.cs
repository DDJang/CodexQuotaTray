namespace CodexQuotaTray.App.Services;

internal enum FirstPresentationOutcome
{
    First,
    Bypassed,
    Coalesced,
}

internal sealed class FirstPresentationGate
{
    private const int Pending = 0;
    private const int Running = 1;
    private const int Completed = 2;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int state;

    internal Task CompletionTask =>
        Volatile.Read(ref state) == Pending
            ? Task.CompletedTask
            : completion.Task;

    internal async Task<FirstPresentationOutcome> PresentAsync(
        Func<bool, bool> setCloaked,
        Action present,
        Func<CancellationToken, Task> waitForReady,
        Func<bool> canReveal,
        Action hide,
        Action revealed,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool alreadyCloaked = false)
    {
        var observed = Interlocked.CompareExchange(ref state, Running, Pending);
        if (observed == Running)
        {
            return FirstPresentationOutcome.Coalesced;
        }

        if (observed == Completed)
        {
            if (!canReveal())
            {
                hide();
                return FirstPresentationOutcome.Bypassed;
            }

            present();
            revealed();
            return FirstPresentationOutcome.Bypassed;
        }

        var shouldReveal = false;
        try
        {
            var cloaked = alreadyCloaked || setCloaked(true);
            present();
            if (cloaked)
            {
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readiness.CancelAfter(timeout);
                try
                {
                    await waitForReady(readiness.Token);
                }
                catch (OperationCanceledException) when (readiness.IsCancellationRequested)
                {
                    // Timeout and shutdown both use the same fail-safe release path.
                }
            }
        }
        finally
        {
            try
            {
                shouldReveal = canReveal();
                if (!shouldReveal)
                {
                    hide();
                }
            }
            finally
            {
                try
                {
                    _ = setCloaked(false);
                }
                finally
                {
                    Volatile.Write(ref state, Completed);
                    try
                    {
                        if (shouldReveal)
                        {
                            revealed();
                        }
                    }
                    finally
                    {
                        completion.TrySetResult();
                    }
                }
            }
        }

        return FirstPresentationOutcome.First;
    }
}
