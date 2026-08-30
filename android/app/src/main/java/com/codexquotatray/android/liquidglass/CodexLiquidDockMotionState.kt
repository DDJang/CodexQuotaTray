// Adapted and extensively modified from Kyant0/AndroidLiquidGlass.
// Reference implementation commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
package com.codexquotatray.android.liquidglass

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.spring
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

@Stable
internal class CodexLiquidDockMotionState(
    initialIndex: Int,
    private val animationScope: CoroutineScope,
) {
    var visualPosition by mutableFloatStateOf(initialIndex.coerceIn(0, 1).toFloat())
        private set
    var pressProgress by mutableFloatStateOf(0f)
        private set
    var normalizedVelocity by mutableFloatStateOf(0f)
        private set
    var dragging by mutableStateOf(false)
        private set

    private var committedIndex = initialIndex.coerceIn(0, 1)
    private var gestureGeneration = 0L
    private val latestSelection = LatestMainDockSelection(committedIndex)
    private var settleJob: Job? = null
    private var pressJob: Job? = null

    fun beginGesture(): Long {
        settleJob?.cancel()
        settleJob = null
        gestureGeneration += 1L
        dragging = true
        normalizedVelocity = 0f
        animatePressTo(1f)
        return gestureGeneration
    }

    fun dragTo(generation: Long, position: Float, velocity: Float) {
        if (!dragging || generation != gestureGeneration) return
        visualPosition = clampMainDockPosition(position)
        normalizedVelocity = velocity.coerceIn(-4f, 4f)
    }

    fun finishDrag(generation: Long, cancelled: Boolean): MainDockDragResult? {
        if (!dragging || generation != gestureGeneration) return null
        dragging = false
        val result = resolveMainDockDrag(committedIndex, visualPosition, cancelled)
        result.committedIndex?.let { committedIndex = it }
        settleTo(result.targetIndex)
        animatePressTo(0f)
        return result
    }

    fun finishTap(generation: Long, requestedIndex: Int): Int? {
        if (!dragging || generation != gestureGeneration) return null
        dragging = false
        val target = requestedIndex.coerceIn(0, 1)
        val commit = mainDockSelectionChange(committedIndex, target)
        if (commit != null) committedIndex = commit
        settleTo(target)
        animatePressTo(0f)
        return commit
    }

    fun cancelGesture(generation: Long) {
        if (!dragging || generation != gestureGeneration) return
        dragging = false
        settleTo(committedIndex)
        animatePressTo(0f)
    }

    fun syncExternalSelection(index: Int) {
        val target = index.coerceIn(0, 1)
        if (target == committedIndex && !dragging && latestSelection.targetIndex == target) return
        committedIndex = target
        gestureGeneration += 1L
        dragging = false
        animatePressTo(0f)
        settleTo(target)
    }

    private fun settleTo(index: Int) {
        val target = index.coerceIn(0, 1)
        val generation = latestSelection.update(target)
        settleJob?.cancel()
        settleJob = animationScope.launch {
            val position = Animatable(visualPosition, visibilityThreshold = 0.001f)
            position.animateTo(
                targetValue = target.toFloat(),
                animationSpec = spring(dampingRatio = 0.72f, stiffness = 700f, visibilityThreshold = 0.001f),
                initialVelocity = normalizedVelocity,
            ) {
                if (!latestSelection.isLatest(generation)) return@animateTo
                visualPosition = clampMainDockPosition(value)
                normalizedVelocity = velocity.coerceIn(-4f, 4f)
            }
            if (latestSelection.isLatest(generation)) {
                visualPosition = target.toFloat()
                normalizedVelocity = 0f
            }
        }
    }

    private fun animatePressTo(target: Float) {
        pressJob?.cancel()
        pressJob = animationScope.launch {
            val progress = Animatable(pressProgress, visibilityThreshold = 0.001f)
            progress.animateTo(
                targetValue = target,
                animationSpec = spring(dampingRatio = 0.72f, stiffness = 700f, visibilityThreshold = 0.001f),
            ) {
                pressProgress = value.coerceIn(0f, 1f)
            }
            pressProgress = target
        }
    }
}
