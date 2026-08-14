package com.codexquotatray.android

import kotlin.math.roundToInt

private const val QUOTA_PROGRESS_RED = 0xFFFF4D5DL
private const val QUOTA_PROGRESS_YELLOW = 0xFFFFD84DL
private const val QUOTA_PROGRESS_GREEN = 0xFF35E66BL

internal fun quotaProgressArgb(remainingPercent: Int): Int {
    val remaining = remainingPercent.coerceIn(0, 100)
    return if (remaining >= 50) {
        interpolateArgb(
            start = QUOTA_PROGRESS_YELLOW,
            end = QUOTA_PROGRESS_GREEN,
            fraction = (remaining - 50) / 50f,
        )
    } else {
        interpolateArgb(
            start = QUOTA_PROGRESS_RED,
            end = QUOTA_PROGRESS_YELLOW,
            fraction = remaining / 50f,
        )
    }
}

private fun interpolateArgb(start: Long, end: Long, fraction: Float): Int {
    val t = fraction.coerceIn(0f, 1f)
    val alpha = interpolateChannel((start ushr 24) and 0xFF, (end ushr 24) and 0xFF, t)
    val red = interpolateChannel((start ushr 16) and 0xFF, (end ushr 16) and 0xFF, t)
    val green = interpolateChannel((start ushr 8) and 0xFF, (end ushr 8) and 0xFF, t)
    val blue = interpolateChannel(start and 0xFF, end and 0xFF, t)
    return (alpha shl 24) or (red shl 16) or (green shl 8) or blue
}

private fun interpolateChannel(start: Long, end: Long, fraction: Float): Int =
    (start + (end - start) * fraction).roundToInt().coerceIn(0, 255)
