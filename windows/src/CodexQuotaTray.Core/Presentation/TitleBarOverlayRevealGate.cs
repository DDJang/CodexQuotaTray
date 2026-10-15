using System.Drawing;

namespace CodexQuotaTray.Core.Presentation;

/// <summary>Bounded settling after a host is shown/restored; no timer once revealed.</summary>
public sealed class TitleBarOverlayRevealGate
{
    public const long MinimumSettleMilliseconds = 350;
    public const long GeometryQuietMilliseconds = 80;
    public const long MaximumSettleMilliseconds = 1000;

    private bool needsSettle = true;
    private long? startedAt;
    private long geometryChangedAt;
    private Rectangle? lastBounds;

    public long? NextCheckAtMilliseconds => startedAt is { } start
        ? Math.Min(start + MaximumSettleMilliseconds,
            Math.Max(start + MinimumSettleMilliseconds, geometryChangedAt + GeometryQuietMilliseconds))
        : null;

    public void Reset()
    {
        needsSettle = true;
        startedAt = null;
        lastBounds = null;
    }

    public void BeginRestore(long nowMilliseconds)
    {
        // Show/foreground/restore notifications may describe the same transition.
        if (startedAt is not null) { return; }
        needsSettle = false;
        startedAt = nowMilliseconds;
        geometryChangedAt = nowMilliseconds;
        lastBounds = null;
    }

    public void GeometryChanged(long nowMilliseconds)
    {
        if (startedAt is not null) { geometryChangedAt = nowMilliseconds; }
    }

    public bool CanReveal(Rectangle bounds, long nowMilliseconds)
    {
        if (needsSettle) { BeginRestore(nowMilliseconds); }
        if (startedAt is null) { return true; }
        if (lastBounds != bounds)
        {
            lastBounds = bounds;
            geometryChangedAt = nowMilliseconds;
        }
        if (nowMilliseconds < NextCheckAtMilliseconds!.Value) { return false; }
        startedAt = null;
        return true;
    }
}
