package com.codexquotatray.android

import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.spring
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
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
import kotlin.math.abs

/**
 * Adds an unbounded rubber-band translation after the scrollable reaches either edge.
 * Continued finger movement always moves the content, with progressively stronger resistance.
 */
@Composable
internal fun Modifier.dampedVerticalOverscroll(): Modifier {
    val resistanceDistance = with(LocalDensity.current) { 180.dp.toPx() }
    var offset by remember { mutableFloatStateOf(0f) }
    val connection = remember(resistanceDistance) {
        object : NestedScrollConnection {
            override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
                if (source != NestedScrollSource.UserInput || offset == 0f || offset * available.y >= 0f) {
                    return Offset.Zero
                }

                val consumed = if (offset > 0f) {
                    available.y.coerceAtLeast(-offset)
                } else {
                    available.y.coerceAtMost(-offset)
                }
                offset += consumed
                return Offset(0f, consumed)
            }

            override fun onPostScroll(
                consumed: Offset,
                available: Offset,
                source: NestedScrollSource,
            ): Offset {
                if (source != NestedScrollSource.UserInput || available.y == 0f) return Offset.Zero

                val resistance = 0.55f / (1f + abs(offset) / resistanceDistance)
                offset += available.y * resistance
                return Offset(0f, available.y)
            }

            override suspend fun onPostFling(consumed: Velocity, available: Velocity): Velocity {
                if (offset != 0f) {
                    animate(
                        initialValue = offset,
                        targetValue = 0f,
                        animationSpec = spring(
                            dampingRatio = Spring.DampingRatioNoBouncy,
                            stiffness = Spring.StiffnessMediumLow,
                        ),
                    ) { value, _ -> offset = value }
                    offset = 0f
                }
                return Velocity.Zero
            }
        }
    }

    return nestedScroll(connection).graphicsLayer { translationY = offset }
}
