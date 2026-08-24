package com.codexquotatray.android

import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.spring
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Velocity
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.sign
import kotlin.math.sqrt

/**
 * Applies an unbounded, progressively damped translation once the scrollable reaches an edge.
 *
 * The state stores the full unconsumed finger distance, while the layer receives only its
 * damped projection. Keeping those quantities separate means a continued drag can never hit a
 * visual hard stop; only the additional visible distance gets smaller as the drag grows.
 */
@Composable
internal fun Modifier.dampedVerticalOverscroll(
    onUpwardOverscrollChanged: (Boolean) -> Unit = {},
): Modifier {
    val resistanceDistance = with(LocalDensity.current) { 180.dp.toPx() }
    val animationScope = rememberCoroutineScope()
    val currentOnUpwardOverscrollChanged by rememberUpdatedState(onUpwardOverscrollChanged)
    var unconsumedDrag by remember { mutableFloatStateOf(0f) }
    var reboundJob by remember { mutableStateOf<Job?>(null) }

    val connection = remember(resistanceDistance, animationScope) {
        object : NestedScrollConnection {
            private fun updateUnconsumedDrag(value: Float) {
                unconsumedDrag = value
                currentOnUpwardOverscrollChanged(value < 0f)
            }

            private fun stopRebound() {
                reboundJob?.cancel()
                reboundJob = null
            }

            private fun startRebound(initialVelocityY: Float = 0f) {
                stopRebound()
                val initialDrag = unconsumedDrag
                reboundJob = animationScope.launch {
                    animate(
                        initialValue = initialDrag,
                        targetValue = 0f,
                        animationSpec = spring(
                            dampingRatio = Spring.DampingRatioNoBouncy,
                            stiffness = Spring.StiffnessMediumLow,
                        ),
                        initialVelocity = initialVelocityY,
                    ) { value, _ ->
                        updateUnconsumedDrag(value)
                    }
                    updateUnconsumedDrag(0f)
                }
            }

            override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                if (
                    source != NestedScrollSource.UserInput ||
                    unconsumedDrag == 0f ||
                    unconsumedDrag * available.y >= 0f
                ) {
                    return Offset.Zero
                }

                stopRebound()
                val consumed = if (unconsumedDrag > 0f) {
                    available.y.coerceAtLeast(-unconsumedDrag)
                } else {
                    available.y.coerceAtMost(-unconsumedDrag)
                }
                updateUnconsumedDrag(unconsumedDrag + consumed)
                return Offset(0f, consumed)
            }

            override fun onPostScroll(
                consumed: Offset,
                available: Offset,
                source: NestedScrollSource,
            ): Offset {
                if (source != NestedScrollSource.UserInput || available.y == 0f) {
                    return Offset.Zero
                }

                stopRebound()
                updateUnconsumedDrag(unconsumedDrag + available.y)
                return Offset(0f, available.y)
            }

            override suspend fun onPostFling(consumed: Velocity, available: Velocity): Velocity {
                val remainingVelocityY = available.y
                if (!shouldStartOverscrollRebound(unconsumedDrag, remainingVelocityY)) {
                    return Velocity.Zero
                }

                startRebound(initialVelocityY = remainingVelocityY)
                return if (remainingVelocityY != 0f) {
                    Velocity(0f, remainingVelocityY)
                } else {
                    Velocity.Zero
                }
            }
        }
    }

    return nestedScroll(connection).graphicsLayer {
        translationY = dampedOverscrollDisplacement(unconsumedDrag, resistanceDistance)
    }
}

internal fun dampedOverscrollDisplacement(
    unconsumedDrag: Float,
    resistanceDistance: Float,
): Float {
    require(resistanceDistance > 0f) { "resistanceDistance must be positive" }
    val magnitude = abs(unconsumedDrag)
    val dampedMagnitude = resistanceDistance *
        (sqrt(1f + 2f * magnitude / resistanceDistance) - 1f)
    return dampedMagnitude * unconsumedDrag.sign
}

internal fun shouldStartOverscrollRebound(
    unconsumedDrag: Float,
    remainingVelocity: Float,
): Boolean = unconsumedDrag != 0f || remainingVelocity != 0f
