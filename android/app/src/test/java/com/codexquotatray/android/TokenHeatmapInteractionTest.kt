package com.codexquotatray.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
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
    fun tooltipStaysAboveMiddleCellWith32DpClearance() {
        val baseBounds = Rect(10f, 100f, 34f, 124f)
        val visualBounds = expandedCellBounds(baseBounds, 1.5f)
        val placement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            cellBounds = baseBounds,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            selectedScale = 1.5f,
            clearancePx = 32f,
        )

        assertEquals(0f, placement.x, 0.001f)
        assertEquals(visualBounds.top - 64f - 32f, placement.y, 0.001f)
        assertEquals(32f, visualBounds.top - placement.y - 64f, 0.001f)
    }

    @Test
    fun tooltipStaysAboveTopCellEvenWhenPlacementIsNegative() {
        val baseBounds = Rect(100f, 20f, 124f, 44f)
        val visualBounds = expandedCellBounds(baseBounds, 1.5f)
        val placement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            cellBounds = baseBounds,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            selectedScale = 1.5f,
            clearancePx = 32f,
        )

        assertEquals(2f, placement.x, 0.001f)
        assertEquals(visualBounds.top - 64f - 32f, placement.y, 0.001f)
        assertTrue(placement.y < 0f)
        assertEquals(32f, visualBounds.top - placement.y - 64f, 0.001f)
    }

    @Test
    fun tooltipPlacementClampsRightEdgeWhileStayingAbove() {
        val baseBounds = Rect(280f, 100f, 304f, 124f)
        val placement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            cellBounds = baseBounds,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            selectedScale = 1.5f,
            clearancePx = 32f,
        )

        assertEquals(80f, placement.x, 0.001f)
        assertEquals(-2f, placement.y, 0.001f)
    }

    @Test
    fun tooltipPlacementUsesExpandedBoundsWhenScaleChanges() {
        val baseBounds = Rect(100f, 100f, 124f, 124f)
        val basePlacement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            cellBounds = baseBounds,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            selectedScale = 1f,
            clearancePx = 32f,
        )
        val selectedPlacement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            cellBounds = baseBounds,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            selectedScale = 1.5f,
            clearancePx = 32f,
        )

        assertEquals(4f, basePlacement.y, 0.001f)
        assertEquals(-2f, selectedPlacement.y, 0.001f)
    }

    @Test
    fun tooltipStaysAboveBottomRowWithTheSameClearance() {
        val baseBounds = Rect(100f, 174f, 124f, 198f)
        val visualBounds = expandedCellBounds(baseBounds, 1.5f)
        val placement = placeHeatmapTooltip(
            viewportWidthPx = 300f,
            cellBounds = baseBounds,
            tooltipWidthPx = 220f,
            tooltipHeightPx = 64f,
            selectedScale = 1.5f,
            clearancePx = 32f,
        )

        assertEquals(visualBounds.top - 64f - 32f, placement.y, 0.001f)
        assertEquals(32f, visualBounds.top - placement.y - 64f, 0.001f)
        assertTrue(placement.y + 32f < visualBounds.center.y)
    }

    @Test
    fun tappingSelectedCellClearsOnlyWhenTheGestureDoesNotChangeDate() {
        val selected = LocalDate.of(2026, 7, 5)
        val sameCell = heatmapGestureOnDown(selected, selected)!!
        val moved = heatmapGestureOnMove(sameCell, selected.plusDays(1))

        assertTrue(heatmapGestureShouldClear(sameCell))
        assertEquals(selected.plusDays(1), moved.currentScrubDate)
        assertTrue(!heatmapGestureShouldClear(moved))
    }

    @Test
    fun blankDownHasNoGestureStateAndValidDownStartsSelection() {
        val selected = LocalDate.of(2026, 7, 5)

        assertNull(heatmapGestureOnDown(selected, null))
        assertEquals(selected, heatmapSelectionAfterHit(selected, null))
        val state = heatmapGestureOnDown(null, selected)!!
        assertEquals(selected, state.gestureStartDate)
        assertEquals(selected, state.currentScrubDate)
        assertTrue(!state.startedOnSelected)
        assertTrue(!heatmapGestureShouldClear(state))
    }

    @Test
    fun selectedCellExpansionCrossesTheFiveDpGap() {
        val expansion = (24f * HEATMAP_SELECTED_SCALE - 24f) / 2f

        assertEquals(36f, 24f * HEATMAP_SELECTED_SCALE, 0.001f)
        assertEquals(6f, expansion, 0.001f)
        assertTrue(expansion > 5f)
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
