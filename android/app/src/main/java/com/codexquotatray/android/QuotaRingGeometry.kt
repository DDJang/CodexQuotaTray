package com.codexquotatray.android

import kotlin.math.asin

/** Shared visual geometry for round-capped quota progress rings. */
internal const val QUOTA_RING_CAP_CLEARANCE_DP = 0.75f

internal fun quotaRingVisualSweepDegrees(
    progress: Float,
    radiusPx: Float,
    strokeWidthPx: Float,
    capClearancePx: Float = 0f,
): Float {
    val safeProgress = when {
        progress.isNaN() || progress <= 0f -> 0f
        progress >= 1f -> 1f
        else -> progress
    }
    if (safeProgress <= 0f) return 0f
    if (safeProgress >= 1f) return 360f
    if (
        radiusPx.isNaN() || radiusPx.isInfinite() || radiusPx <= 0f ||
        strokeWidthPx.isNaN() || strokeWidthPx.isInfinite() || strokeWidthPx <= 0f ||
        capClearancePx.isNaN() || capClearancePx.isInfinite()
    ) {
        return 0f
    }

    val clearance = capClearancePx.coerceAtLeast(0f)
    val chordRatio = (
        (strokeWidthPx.toDouble() + clearance.toDouble()) /
            (2.0 * radiusPx.toDouble())
        ).coerceIn(0.0, 1.0)
    val minimumGapDegrees = Math.toDegrees(2.0 * asin(chordRatio)).toFloat()
    val maxSafeSweep = (360f - minimumGapDegrees).coerceAtLeast(0f)
    return minOf(safeProgress * 360f, maxSafeSweep)
}
