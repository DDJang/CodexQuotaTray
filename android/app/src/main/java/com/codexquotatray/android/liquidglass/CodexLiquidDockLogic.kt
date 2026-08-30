package com.codexquotatray.android.liquidglass

import kotlin.math.roundToInt

internal fun clampMainDockPosition(position: Float): Float = position.coerceIn(0f, 1f)

internal fun nearestMainDockTab(position: Float): Int = clampMainDockPosition(position).roundToInt()

internal fun mainDockSelectionChange(committedIndex: Int, requestedIndex: Int): Int? {
    val target = requestedIndex.coerceIn(0, 1)
    return target.takeIf { it != committedIndex.coerceIn(0, 1) }
}

internal fun mainDockPositionDelta(physicalDeltaFraction: Float, isLeftToRight: Boolean): Float =
    if (isLeftToRight) physicalDeltaFraction else -physicalDeltaFraction

internal fun mainDockTabAtPhysicalPosition(positionFraction: Float, isLeftToRight: Boolean): Int {
    val physicalIndex = if (positionFraction < 0.5f) 0 else 1
    return if (isLeftToRight) physicalIndex else 1 - physicalIndex
}

internal fun mainDockSelectorOffset(position: Float, availableWidth: Float, isLeftToRight: Boolean): Float {
    val logicalPosition = clampMainDockPosition(position)
    return if (isLeftToRight) logicalPosition * availableWidth else (1f - logicalPosition) * availableWidth
}

internal data class MainDockDragResult(
    val targetIndex: Int,
    val committedIndex: Int?,
)

internal fun resolveMainDockDrag(
    committedIndex: Int,
    visualPosition: Float,
    cancelled: Boolean,
): MainDockDragResult {
    val committed = committedIndex.coerceIn(0, 1)
    val target = if (cancelled) committed else nearestMainDockTab(visualPosition)
    return MainDockDragResult(
        targetIndex = target,
        committedIndex = if (cancelled) null else mainDockSelectionChange(committed, target),
    )
}

internal class LatestMainDockSelection(initialIndex: Int) {
    var targetIndex: Int = initialIndex.coerceIn(0, 1)
        private set
    var generation: Long = 0L
        private set

    fun update(index: Int): Long {
        targetIndex = index.coerceIn(0, 1)
        generation += 1L
        return generation
    }

    fun isLatest(candidateGeneration: Long): Boolean = candidateGeneration == generation
}
