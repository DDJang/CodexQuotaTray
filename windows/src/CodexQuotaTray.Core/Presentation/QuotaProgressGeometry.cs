namespace CodexQuotaTray.Core.Presentation;

public readonly record struct QuotaProgressGeometry(double Left, double Width)
{
    public static QuotaProgressGeometry Calculate(double availableWidth, double percent)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return new QuotaProgressGeometry(0, 0);
        }

        var clampedPercent = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
        return new QuotaProgressGeometry(0, availableWidth * clampedPercent / 100);
    }
}
