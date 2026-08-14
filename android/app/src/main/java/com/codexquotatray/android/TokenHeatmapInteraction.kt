package com.codexquotatray.android

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerInputScope
import java.time.LocalDate
import kotlin.math.max

internal const val TOKEN_HEATMAP_COLUMNS = 13
internal const val TOKEN_HEATMAP_ROWS = 7

internal data class HeatmapGeometry(
    val cellSizePx: Float,
    val gapPx: Float,
    val startDate: LocalDate,
    val dayCount: Int,
    val contentOffsetX: Float = 0f,
    val rowCount: Int = TOKEN_HEATMAP_ROWS,
) {
    val columnCount: Int get() = TOKEN_HEATMAP_COLUMNS
    val stridePx: Float get() = cellSizePx + gapPx

    val contentWidthPx: Float
        get() = if (columnCount <= 0) 0f else columnCount * cellSizePx + (columnCount - 1) * gapPx

    val contentHeightPx: Float
        get() = if (rowCount <= 0) 0f else rowCount * cellSizePx + (rowCount - 1) * gapPx

    fun cellBounds(index: Int): Rect? {
        if (index !in 0 until dayCount || rowCount <= 0 || columnCount <= 0) return null
        val column = index / rowCount
        val row = index % rowCount
        val left = contentOffsetX + column * stridePx
        val top = row * stridePx
        return Rect(left, top, left + cellSizePx, top + cellSizePx)
    }

    fun cellCenter(index: Int): Offset? = cellBounds(index)?.center

    fun hitTest(point: Offset): Int? {
        if (point.x.isNaN() || point.y.isNaN() || rowCount <= 0 || columnCount <= 0) return null
        val contentX = point.x - contentOffsetX
        if (contentX < 0f || point.y < 0f) return null

        val column = (contentX / stridePx).toInt()
        val row = (point.y / stridePx).toInt()
        if (column !in 0 until columnCount || row !in 0 until rowCount) return null

        val cellX = contentX - column * stridePx
        val cellY = point.y - row * stridePx
        if (cellX >= cellSizePx || cellY >= cellSizePx) return null

        val index = column * rowCount + row
        return index.takeIf { it in 0 until dayCount }
    }

    fun indexToDate(index: Int): LocalDate? =
        index.takeIf { it in 0 until dayCount }?.let { startDate.plusDays(it.toLong()) }
}

internal fun centeredHeatmapOffset(viewportWidthPx: Float, contentWidthPx: Float): Float =
    max(0f, (viewportWidthPx - contentWidthPx) / 2f)

internal data class HeatmapTooltipPlacement(
    val x: Float,
    val y: Float,
)

internal fun expandedCellBounds(
    baseBounds: Rect,
    scale: Float,
): Rect {
    val scaledWidth = baseBounds.width * scale
    val scaledHeight = baseBounds.height * scale
    val center = baseBounds.center
    return Rect(
        left = center.x - scaledWidth / 2f,
        top = center.y - scaledHeight / 2f,
        right = center.x + scaledWidth / 2f,
        bottom = center.y + scaledHeight / 2f,
    )
}

internal fun placeHeatmapTooltip(
    viewportWidthPx: Float,
    containerHeightPx: Float,
    cellBounds: Rect,
    tooltipWidthPx: Float,
    tooltipHeightPx: Float,
    selectedScale: Float,
    clearancePx: Float,
): HeatmapTooltipPlacement {
    val cellCenterX = cellBounds.center.x
    val maxX = max(0f, viewportWidthPx - tooltipWidthPx)
    val x = (cellCenterX - tooltipWidthPx / 2f).coerceIn(0f, maxX)

    val visualBounds = expandedCellBounds(cellBounds, selectedScale)
    val above = visualBounds.top - tooltipHeightPx - clearancePx
    val below = visualBounds.bottom + clearancePx
    val maxY = max(0f, containerHeightPx - tooltipHeightPx)
    val y = when {
        above >= 0f -> above
        below + tooltipHeightPx <= containerHeightPx -> below
        else -> below.coerceIn(0f, maxY)
    }
    return HeatmapTooltipPlacement(x = x, y = y)
}

/** A gap does not clear the active date while the finger is scrubbing. */
internal fun heatmapSelectionAfterHit(previous: LocalDate?, hitDate: LocalDate?): LocalDate? =
    hitDate ?: previous

/**
 * A heatmap gesture starts date selection immediately on a valid cell and
 * consumes subsequent movement until the pointer is released.
 */
internal suspend fun PointerInputScope.detectTokenHeatmapGestures(
    onSelectionStart: (Offset) -> Boolean,
    onSelectionMove: (Offset) -> Unit,
    onSelectionEnd: () -> Unit,
) {
    awaitEachGesture {
        val down = awaitFirstDown(
            requireUnconsumed = false,
            pass = PointerEventPass.Initial,
        )
        val active = onSelectionStart(down.position)
        try {
            while (true) {
                val change = awaitPointerEvent(PointerEventPass.Initial).changes
                    .firstOrNull { it.id == down.id }
                    ?: break
                if (!change.pressed) break
                if (active) {
                    change.consume()
                    onSelectionMove(change.position)
                }
            }
        } finally {
            if (active) onSelectionEnd()
        }
    }
}
