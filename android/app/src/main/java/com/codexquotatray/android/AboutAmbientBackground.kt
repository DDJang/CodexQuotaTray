package com.codexquotatray.android

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import kotlin.math.PI
import kotlin.math.floor
import kotlin.math.sin

private val ABOUT_AURORA_COLORS = listOf(
    Color(0xFF6E8EF5),
    Color(0xFF8B4FD8),
    Color(0xFFD04070),
    Color(0xFFE05535),
    Color(0xFFE07820),
)

internal fun aboutAmbientBaseColor(dark: Boolean): Color =
    if (dark) Color(0xFF0A0A0A) else Color(0xFFFAFAFA)

@Composable
internal fun AboutAmbientBackground(
    dark: Boolean,
    modifier: Modifier = Modifier,
) {
    val isVisible = rememberIsVisible()
    val transition = rememberInfiniteTransition(label = "aboutAurora")
    val progress by transition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 15_000, easing = LinearEasing),
            repeatMode = RepeatMode.Reverse,
        ),
        label = "aboutAuroraProgress",
    )
    val breathe by transition.animateFloat(
        initialValue = 0f,
        targetValue = (2f * PI).toFloat(),
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 4_000, easing = LinearEasing),
            repeatMode = RepeatMode.Reverse,
        ),
        label = "aboutAuroraBreathe",
    )

    Canvas(modifier) {
        val width = size.width
        val height = size.height
        val radius = minOf(width, height)

        drawRect(color = aboutAmbientBaseColor(dark))

        if (!isVisible) return@Canvas

        val theta = progress * (2f * PI.toFloat())
        val breatheStrength = 0.85f + 0.15f * sin(breathe)
        val alpha = (if (dark) 0.35f else 0.18f) * breatheStrength

        val firstColor = interpolateLoopColor(ABOUT_AURORA_COLORS, progress, offset = 0)
        drawRect(
            brush = Brush.radialGradient(
                colors = listOf(firstColor.copy(alpha = alpha), Color.Transparent),
                center = Offset(
                    width * (0.20f + 0.15f * sin(theta)),
                    height * (0.25f + 0.15f * sin(theta * 0.70f)),
                ),
                radius = radius * 0.90f,
            ),
        )

        val secondColor = interpolateLoopColor(ABOUT_AURORA_COLORS, progress, offset = 2)
        drawRect(
            brush = Brush.radialGradient(
                colors = listOf(secondColor.copy(alpha = alpha * 0.90f), Color.Transparent),
                center = Offset(
                    width * (0.80f + 0.15f * sin(theta * 1.30f)),
                    height * (0.30f + 0.15f * sin(theta * 0.90f)),
                ),
                radius = radius * 0.85f,
            ),
        )

        val thirdColor = interpolateLoopColor(ABOUT_AURORA_COLORS, progress, offset = 4)
        drawRect(
            brush = Brush.radialGradient(
                colors = listOf(thirdColor.copy(alpha = alpha * 0.85f), Color.Transparent),
                center = Offset(
                    width * (0.50f + 0.20f * sin(theta * 0.80f)),
                    height * (0.70f + 0.12f * sin(theta * 1.10f)),
                ),
                radius = radius * 0.90f,
            ),
        )
    }
}

@Composable
private fun rememberIsVisible(): Boolean {
    val lifecycleOwner = LocalLifecycleOwner.current
    var isVisible by remember(lifecycleOwner) {
        mutableStateOf(lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED))
    }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, _ ->
            isVisible = lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    return isVisible
}

internal fun interpolateLoopColor(
    colors: List<Color>,
    progress: Float,
    offset: Int,
): Color {
    if (colors.isEmpty()) return Color.Transparent

    val scaled = progress.coerceIn(0f, 1f) * colors.size
    val segment = floor(scaled).toInt()
    val index = (segment + offset) % colors.size
    val nextIndex = (index + 1) % colors.size
    val fraction = scaled - segment.toFloat()
    return lerp(colors[index], colors[nextIndex], fraction)
}
