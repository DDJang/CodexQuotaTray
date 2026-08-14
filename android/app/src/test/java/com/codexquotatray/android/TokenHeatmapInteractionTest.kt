package com.codexquotatray.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.LocalDate

class TokenHeatmapInteractionTest {
    private val start = LocalDate.of(2026, 8, 2)
    private val geometry = HeatmapGeometry(
        cellSizePx = 22f,
        gapPx = 5f,
        startDate = start,
        dayCount = 85,
    )

    @Test
    fun cellBoundsAndCentersFollowColumnThenWeekdayRows() {
        assertEquals(Rect(0f, 0f, 22f, 22f), geometry.cellBounds(0))
        assertEquals(Rect(27f, 0f, 49f, 22f), geometry.cellBounds(7))
        assertEquals(Offset(11f, 11f), geometry.cellCenter(0))
        assertEquals(start.plusDays(7), geometry.indexToDate(7))
        assertEquals(13, geometry.columnCount)
        assertEquals(346f, geometry.contentWidthPx, 0.001f)
        assertEquals(184f, geometry.contentHeightPx, 0.001f)
    }

    @Test
    fun hitTestRejectsGapsAndOutOfRangeCells() {
        assertEquals(0, geometry.hitTest(Offset(11f, 11f)))
        assertEquals(7, geometry.hitTest(Offset(38f, 11f)))
        assertNull(geometry.hitTest(Offset(24f, 11f)))
        assertNull(geometry.hitTest(Offset(11f, 24f)))
        assertNull(geometry.hitTest(Offset(335f, 38f)))
        assertNull(geometry.hitTest(Offset(400f, 11f)))
    }

    @Test
    fun dynamicCellsAreLargerThanThePreviousFixedSize() {
        assertEquals(22f, geometry.cellSizePx, 0.001f)
        assertEquals(5f, geometry.gapPx, 0.001f)
    }

    @Test
    fun centeredContentOnlyAddsOffsetWhenItFits() {
        assertEquals(50f, centeredHeatmapOffset(200f, 100f), 0.001f)
        assertEquals(0f, centeredHeatmapOffset(100f, 200f), 0.001f)
    }

    @Test
    fun tooltipStaysInViewportAndPrefersSpaceAboveCell() {
        val placement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            containerHeightPx = 250f,
            cellBounds = Rect(10f, 100f, 28f, 118f),
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            topReservePx = 72f,
            gapPx = 8f,
        )

        assertEquals(0f, placement.x, 0.001f)
        assertEquals(100f, placement.y, 0.001f)
    }

    @Test
    fun tooltipFallsBelowTopCellWhenThereIsNoSpaceAbove() {
        val placement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            containerHeightPx = 250f,
            cellBounds = Rect(100f, 0f, 118f, 18f),
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            topReservePx = 0f,
            gapPx = 8f,
        )

        assertEquals(0f, placement.x, 0.001f)
        assertEquals(26f, placement.y, 0.001f)
    }

    @Test
    fun scrubbingKeepsTheLastDateWhenFingerCrossesAGap() {
        val first = start
        val second = start.plusDays(7)
        val third = start.plusDays(14)

        assertEquals(first, heatmapSelectionAfterHit(null, first))
        assertEquals(second, heatmapSelectionAfterHit(first, second))
        assertEquals(second, heatmapSelectionAfterHit(second, null))
        assertEquals(third, heatmapSelectionAfterHit(second, third))
    }
}
