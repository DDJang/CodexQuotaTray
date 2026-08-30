package com.codexquotatray.android

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.key
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.util.lerp

internal data class MainPageEnterTransition(
    val shouldAnimate: Boolean,
    val direction: Int,
)

internal fun mainPageEnterTransition(previousIndex: Int?, selectedIndex: Int): MainPageEnterTransition =
    MainPageEnterTransition(
        shouldAnimate = previousIndex != null && previousIndex != selectedIndex,
        direction = when {
            previousIndex == null || previousIndex == selectedIndex -> 0
            selectedIndex > previousIndex -> 1
            else -> -1
        },
    )

private class MainPageHistory {
    var previousIndex: Int? = null
}

@Composable
internal fun MainPageSwitcher(
    selectedIndex: Int,
    modifier: Modifier = Modifier,
    content: @Composable (Int, Modifier) -> Unit,
) {
    val history = remember { MainPageHistory() }

    key(selectedIndex) {
        val transition = remember {
            mainPageEnterTransition(history.previousIndex, selectedIndex)
        }
        val progress = remember {
            Animatable(if (transition.shouldAnimate) 0f else 1f, visibilityThreshold = 0.001f)
        }
        LaunchedEffect(Unit) {
            if (transition.shouldAnimate) {
                progress.animateTo(1f, tween(durationMillis = 150))
            } else {
                progress.snapTo(1f)
            }
        }
        content(
            selectedIndex,
            modifier.graphicsLayer {
                alpha = lerp(0.94f, 1f, progress.value)
                translationX = transition.direction * size.width * 0.03f * (1f - progress.value)
            },
        )
        SideEffect {
            history.previousIndex = selectedIndex
        }
    }
}
