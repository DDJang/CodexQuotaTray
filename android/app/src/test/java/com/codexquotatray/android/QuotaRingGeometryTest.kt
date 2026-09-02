package com.codexquotatray.android

import kotlin.math.sin
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaRingGeometryTest {
    @Test
    fun zeroAndNormalProgressKeepTheirExpectedSweep() {
        assertEquals(0f, quotaRingVisualSweepDegrees(0f, 50f, 10f, 0.75f), 0f)
        assertEquals(180f, quotaRingVisualSweepDegrees(0.5f, 50f, 10f, 0.75f), 0.001f)
    }

    @Test
    fun nearFullProgressKeepsRoundCapEndpointsSeparated() {
        val radius = 50f
        val strokeWidth = 10f
        val clearance = 0.75f

        listOf(0.98f, 0.99f).forEach { progress ->
            val visualSweep = quotaRingVisualSweepDegrees(
                progress = progress,
                radiusPx = radius,
                strokeWidthPx = strokeWidth,
                capClearancePx = clearance,
            )
            val rawSweep = progress * 360f
            val gapRadians = Math.toRadians((360f - visualSweep).toDouble())
            val endpointChord = 2.0 * radius * sin(gapRadians / 2.0)

            assertTrue(visualSweep < rawSweep)
            assertTrue(endpointChord >= strokeWidth + clearance - 0.001)
        }
    }

    @Test
    fun completeProgressRemainsAFullCircle() {
        assertEquals(360f, quotaRingVisualSweepDegrees(1f, 50f, 10f, 0.75f), 0f)
    }

    @Test
    fun progressIsDefensivelyClamped() {
        assertEquals(0f, quotaRingVisualSweepDegrees(-1f, 50f, 10f), 0f)
        assertEquals(0f, quotaRingVisualSweepDegrees(Float.NaN, 50f, 10f), 0f)
        assertEquals(360f, quotaRingVisualSweepDegrees(1.5f, 50f, 10f), 0f)
    }

    @Test
    fun invalidGeometryNeverProducesNonFiniteSweep() {
        val invalidSweeps = listOf(
            quotaRingVisualSweepDegrees(0.5f, 0f, 10f),
            quotaRingVisualSweepDegrees(0.5f, -1f, 10f),
            quotaRingVisualSweepDegrees(0.5f, 50f, 0f),
            quotaRingVisualSweepDegrees(0.5f, 50f, -1f),
            quotaRingVisualSweepDegrees(0.5f, Float.NaN, 10f),
            quotaRingVisualSweepDegrees(0.5f, Float.POSITIVE_INFINITY, 10f),
            quotaRingVisualSweepDegrees(0.5f, 50f, Float.NaN),
            quotaRingVisualSweepDegrees(0.5f, 50f, Float.POSITIVE_INFINITY),
        )

        invalidSweeps.forEach { sweep ->
            assertFalse(sweep.isNaN())
            assertFalse(sweep.isInfinite())
            assertEquals(0f, sweep, 0f)
        }
    }
}
