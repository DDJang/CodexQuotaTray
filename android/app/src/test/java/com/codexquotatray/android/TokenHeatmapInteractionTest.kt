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
        cellSizePx = 18f,
        gapPx = 4f,
        columnCount = 3,
        startDate = start,
        dayCount = 20,
        contentOffsetX = 10f,
    )

    @Test
    fun cellBoundsAndCentersFollowColumnThenWeekdayRows() {
        assertEquals(Rect(10f, 0f, 28f, 18f), geometry.cellBounds(0))
        assertEquals(Rect(32f, 0f, 50f, 18f), geometry.cellBounds(7))
        assertEquals(Offset(19f, 9f), geometry.cellCenter(0))
        assertEquals(start.plusDays(7), geometry.indexToDate(7))
    }

    @Test
    fun hitTestRejectsGapsAndOutOfRangeCells() {
        assertEquals(0, geometry.hitTest(Offset(19f, 9f), horizontalScrollPx = 0f))
        assertEquals(7, geometry.hitTest(Offset(41f, 9f), horizontalScrollPx = 0f))
        assertNull(geometry.hitTest(Offset(30f, 9f), horizontalScrollPx = 0f))
        assertNull(geometry.hitTest(Offset(19f, 20f), horizontalScrollPx = 0f))
        assertNull(geometry.hitTest(Offset(100f, 9f), horizontalScrollPx = 0f))
    }

    @Test
    fun hitTestAccountsForHorizontalScroll() {
        assertEquals(7, geometry.hitTest(Offset(19f, 9f), horizontalScrollPx = 22f))
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
            horizontalScrollPx = 0f,
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
            horizontalScrollPx = 0f,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            topReservePx = 0f,
            gapPx = 8f,
        )

        assertEquals(0f, placement.x, 0.001f)
        assertEquals(26f, placement.y, 0.001f)
    }
}
