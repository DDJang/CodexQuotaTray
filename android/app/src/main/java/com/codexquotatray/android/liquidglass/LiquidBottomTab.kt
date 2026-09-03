// Adapted and modified from Kyant0/AndroidLiquidGlass.
// Pinned commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
package com.codexquotatray.android.liquidglass

import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.PressInteraction
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.kyant.shapes.Capsule
import kotlinx.coroutines.flow.collect

internal val LocalLiquidBottomTabScale = staticCompositionLocalOf { { 1f } }
internal val LocalLiquidBottomTabInteraction = staticCompositionLocalOf {
    LiquidBottomTabInteractionCallbacks()
}

internal data class LiquidBottomTabInteractionCallbacks(
    val onPress: (index: Int, press: PressInteraction.Press) -> Unit = { _, _ -> },
    val onRelease: (index: Int, press: PressInteraction.Press) -> Unit = { _, _ -> },
    val onCancel: (index: Int, press: PressInteraction.Press) -> Unit = { _, _ -> },
    val onClick: (index: Int) -> Unit = {},
    val onDragStart: (index: Int) -> Boolean = { false },
    val onDrag: (index: Int, dragAmountX: Float) -> Unit = { _, _ -> },
    val onDragEnd: (index: Int) -> Unit = {},
    val onDragCancel: (index: Int) -> Unit = {},
)

@Composable
fun RowScope.LiquidBottomTab(
    tabIndex: Int,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val scale = LocalLiquidBottomTabScale.current
    val interactionCallbacks = LocalLiquidBottomTabInteraction.current
    val interactionCallbacksState = rememberUpdatedState(interactionCallbacks)
    val interactionSource = remember { MutableInteractionSource() }
    LaunchedEffect(interactionSource) {
        val activePresses = mutableListOf<PressInteraction.Press>()
        interactionSource.interactions.collect { interaction ->
            when (interaction) {
                is PressInteraction.Press -> {
                    activePresses += interaction
                    interactionCallbacksState.value.onPress(tabIndex, interaction)
                }

                is PressInteraction.Release -> {
                    val press = activePresses.firstOrNull { it === interaction.press } ?: return@collect
                    activePresses.remove(press)
                    interactionCallbacksState.value.onRelease(tabIndex, press)
                }

                is PressInteraction.Cancel -> {
                    val press = activePresses.firstOrNull { it === interaction.press } ?: return@collect
                    activePresses.remove(press)
                    interactionCallbacksState.value.onCancel(tabIndex, press)
                }
            }
        }
    }
    Column(
        modifier
            .clip(Capsule())
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                role = Role.Tab,
                onClick = {
                    interactionCallbacksState.value.onClick(tabIndex)
                    onClick()
                },
            )
            .pointerInput(tabIndex) {
                var dragClaimed = false
                detectDragGestures(
                    onDragStart = {
                        dragClaimed = interactionCallbacksState.value.onDragStart(tabIndex)
                    },
                    onDragEnd = {
                        if (dragClaimed) {
                            interactionCallbacksState.value.onDragEnd(tabIndex)
                        }
                        dragClaimed = false
                    },
                    onDragCancel = {
                        if (dragClaimed) {
                            interactionCallbacksState.value.onDragCancel(tabIndex)
                        }
                        dragClaimed = false
                    },
                ) { _, dragAmount ->
                    if (dragClaimed) {
                        interactionCallbacksState.value.onDrag(tabIndex, dragAmount.x)
                    }
                }
            }
            .fillMaxHeight()
            .weight(1f)
            .graphicsLayer {
                val scale = scale()
                scaleX = scale
                scaleY = scale
            },
        verticalArrangement = Arrangement.spacedBy(2f.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
        content = content,
    )
}
