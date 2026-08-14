package com.codexquotatray.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerInputScope
import kotlinx.coroutines.withTimeoutOrNull
import java.time.LocalDate
import kotlin.math.max

internal data class HeatmapGeometry(
    val cellSizePx: Float,
    val gapPx: Float,
    val columnCount: Int,
    val startDate: LocalDate,
    val dayCount: Int,
    val rowCount: Int = 7,
    val contentOffsetX: Float = 0f,
) {
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

    fun hitTest(point: Offset, horizontalScrollPx: Float): Int? {
        if (point.x.isNaN() || point.y.isNaN() || rowCount <= 0 || columnCount <= 0) return null
        val contentX = point.x + horizontalScrollPx - contentOffsetX
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

internal fun placeHeatmapTooltip(
    viewportWidthPx: Float,
    containerHeightPx: Float,
    cellBounds: Rect,
    horizontalScrollPx: Float,
    tooltipWidthPx: Float,
    tooltipHeightPx: Float,
    topReservePx: Float,
    gapPx: Float,
): HeatmapTooltipPlacement {
    val cellLeft = cellBounds.left - horizontalScrollPx
    val cellCenterX = cellLeft + cellBounds.width / 2f
    val maxX = max(0f, viewportWidthPx - tooltipWidthPx)
    val x = (cellCenterX - tooltipWidthPx / 2f).coerceIn(0f, maxX)

    val cellTop = cellBounds.top + topReservePx
    val above = cellTop - tooltipHeightPx - gapPx
    val below = cellTop + cellBounds.height + gapPx
    val preferredY = if (above >= 0f) above else below
    val maxY = max(0f, containerHeightPx - tooltipHeightPx)
    return HeatmapTooltipPlacement(x = x, y = preferredY.coerceIn(0f, maxY))
}

/**
 * Leaves ordinary taps and drags to the scroll parents until the system long
 * press timeout has elapsed. Once scrub starts, movement is consumed locally.
 */
internal suspend fun PointerInputScope.detectTokenHeatmapGestures(
    touchSlop: Float,
    longPressTimeoutMillis: Long,
    onTap: (Offset) -> Unit,
    onScrubStart: (Offset) -> Boolean,
    onScrubMove: (Offset) -> Unit,
    onScrubEnd: () -> Unit,
) {
    awaitEachGesture {
        val down = awaitFirstDown(
            requireUnconsumed = false,
            pass = PointerEventPass.Initial,
        )
        val pointerId = down.id
        var lastPosition = down.position
        var movedBeyondSlop = false

        val endedBeforeLongPress: Boolean = withTimeoutOrNull<Boolean>(longPressTimeoutMillis) {
            var ended = false
            while (!ended) {
                val change = awaitPointerEvent(PointerEventPass.Initial).changes.firstOrNull { it.id == pointerId }
                if (change == null || !change.pressed) {
                    if (!movedBeyondSlop) onTap(lastPosition)
                    ended = true
                } else {
                    lastPosition = change.position
                    if ((lastPosition - down.position).getDistance() > touchSlop) {
                        movedBeyondSlop = true
                        ended = true
                    }
                }
            }
            ended
        } ?: false

        if (!endedBeforeLongPress && !movedBeyondSlop && onScrubStart(lastPosition)) {
            try {
                while (true) {
                    val change = awaitPointerEvent(PointerEventPass.Initial).changes.firstOrNull { it.id == pointerId }
                        ?: break
                    if (!change.pressed) break
                    change.consume()
                    lastPosition = change.position
                    onScrubMove(lastPosition)
                }
            } finally {
                onScrubEnd()
            }
        }
    }
}
