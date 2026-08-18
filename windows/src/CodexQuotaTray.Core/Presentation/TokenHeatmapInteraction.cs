namespace CodexQuotaTray.Core.Presentation;

public static class TokenHeatmapInteraction
{
    public const int RowCount = 7;
    public const float CellSize = 17f;
    public const float Gap = 4f;
    public const float TooltipClearance = 8f;
    public static float TooltipSpringDampingRatio => 0.82f;
    public static int TooltipSpringPeriodMilliseconds => 110;
    public const int TooltipFadeDurationMilliseconds = 160;
    public static float HitSurfaceInset => Gap / 2f;
    public static float SelectedScale => 1.5f;

    public static int? HitTest(float x, float y, int cellCount)
    {
        if (cellCount <= 0 || float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
        {
            return null;
        }

        var columnCount = (cellCount + RowCount - 1) / RowCount;
        var stride = CellSize + Gap;
        var contentWidth = columnCount * CellSize + (columnCount - 1) * Gap;
        var contentHeight = RowCount * CellSize + (RowCount - 1) * Gap;
        var halfGap = Gap / 2f;

        if (x < -halfGap
            || y < -halfGap
            || x >= contentWidth + halfGap
            || y >= contentHeight + halfGap)
        {
            return null;
        }

        // Keep the midpoint rule deterministic: an exact gap midpoint belongs
        // to the following column or row, matching the Android geometry hit-test.
        var column = (int)((x + halfGap) / stride);
        var row = (int)((y + halfGap) / stride);
        var index = column * RowCount + row;
        return index < cellCount ? index : null;
    }

    public static TokenHeatmapTooltipPlacement PlaceTooltipAboveCell(
        int cellIndex,
        int cellCount,
        float tooltipWidth,
        float tooltipHeight,
        float heatmapOriginX = 0f,
        float heatmapOriginY = 0f)
    {
        if (cellCount <= 0 || cellIndex < 0 || cellIndex >= cellCount)
        {
            return new TokenHeatmapTooltipPlacement(0f, 0f);
        }

        var column = cellIndex / RowCount;
        var row = cellIndex % RowCount;
        var stride = CellSize + Gap;
        var cellLeft = heatmapOriginX + (column * stride);
        var cellTop = heatmapOriginY + (row * stride);
        var scaledCellSize = CellSize * SelectedScale;
        var scaleInset = (scaledCellSize - CellSize) / 2f;
        var selectedTop = cellTop - scaleInset;
        var cellCenter = cellLeft + (CellSize / 2f);
        var x = cellCenter - (tooltipWidth / 2f);
        var y = selectedTop - tooltipHeight - TooltipClearance;

        return new TokenHeatmapTooltipPlacement(
            x,
            y);
    }
}

public readonly record struct TokenHeatmapTooltipPlacement(float X, float Y);
