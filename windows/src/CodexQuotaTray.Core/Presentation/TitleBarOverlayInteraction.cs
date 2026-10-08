using System.Drawing;

namespace CodexQuotaTray.Core.Presentation;

public sealed class TitleBarOverlayRefreshGate
{
    private int inFlight;

    public bool IsInFlight => Volatile.Read(ref inFlight) != 0;

    public bool TryBegin() => Interlocked.CompareExchange(ref inFlight, 1, 0) == 0;

    public void Complete() => Interlocked.Exchange(ref inFlight, 0);
}

public static class TitleBarOverlayInteraction
{
    public static bool IsClick(Point down, Point up, Size clientSize, uint dpi)
    {
        if (dpi == 0 || !new Rectangle(Point.Empty, clientSize).Contains(up)) { return false; }
        var tolerance = Math.Max(1, (int)Math.Ceiling(4 * dpi / 96d));
        return Math.Abs((long)down.X - up.X) <= tolerance && Math.Abs((long)down.Y - up.Y) <= tolerance;
    }

    public static void ComposeTextPixels(Span<byte> bgra)
    {
        if (bgra.Length % 4 != 0) { throw new ArgumentException("Expected 32-bit BGRA pixels.", nameof(bgra)); }
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            var alpha = Math.Max(1, (int)Math.Max(bgra[offset], Math.Max(bgra[offset + 1], bgra[offset + 2])));
            // White GDI glyph mask -> premultiplied menu-gray text. Alpha 1 in
            // empty areas makes the whole text rectangle clickable without a box.
            bgra[offset] = (byte)(0x90 * alpha / 255);
            bgra[offset + 1] = (byte)(0x8F * alpha / 255);
            bgra[offset + 2] = (byte)(0x8E * alpha / 255);
            bgra[offset + 3] = (byte)alpha;
        }
    }
}
