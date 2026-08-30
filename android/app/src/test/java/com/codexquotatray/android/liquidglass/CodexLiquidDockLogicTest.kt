package com.codexquotatray.android.liquidglass

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CodexLiquidDockLogicTest {
    @Test
    fun positionIsClampedToTwoTabRange() {
        assertEquals(0f, clampMainDockPosition(-0.5f))
        assertEquals(0.4f, clampMainDockPosition(0.4f))
        assertEquals(1f, clampMainDockPosition(1.5f))
    }

    @Test
    fun dragReleaseCommitsNearestTabOnce() {
        assertEquals(0, nearestMainDockTab(0.49f))
        assertEquals(1, nearestMainDockTab(0.5f))
        assertEquals(MainDockDragResult(targetIndex = 1, committedIndex = 1), resolveMainDockDrag(0, 0.8f, false))
    }

    @Test
    fun sameTabSelectionIsSuppressed() {
        assertNull(mainDockSelectionChange(committedIndex = 0, requestedIndex = 0))
        assertEquals(1, mainDockSelectionChange(committedIndex = 0, requestedIndex = 1))
    }

    @Test
    fun cancelledDragReturnsToExternalCommitWithoutCallback() {
        assertEquals(
            MainDockDragResult(targetIndex = 1, committedIndex = null),
            resolveMainDockDrag(committedIndex = 1, visualPosition = 0.1f, cancelled = true),
        )
    }

    @Test
    fun externalSelectionAndRapidUpdatesKeepOnlyLatestGeneration() {
        val latest = LatestMainDockSelection(initialIndex = 0)
        val first = latest.update(1)
        val second = latest.update(0)
        val third = latest.update(1)

        assertFalse(latest.isLatest(first))
        assertFalse(latest.isLatest(second))
        assertTrue(latest.isLatest(third))
        assertEquals(1, latest.targetIndex)
    }

    @Test
    fun rtlReversesGestureAndSelectorPositionMath() {
        assertEquals(0.25f, mainDockPositionDelta(0.25f, isLeftToRight = true))
        assertEquals(-0.25f, mainDockPositionDelta(0.25f, isLeftToRight = false))
        assertEquals(0, mainDockTabAtPhysicalPosition(0.25f, isLeftToRight = true))
        assertEquals(1, mainDockTabAtPhysicalPosition(0.25f, isLeftToRight = false))
        assertEquals(75f, mainDockSelectorOffset(0.25f, 100f, isLeftToRight = false))
    }
}
