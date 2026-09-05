// Adapted and modified from Kyant0/AndroidLiquidGlass.
// Pinned commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
package com.codexquotatray.android.liquidglass

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.VisibilityThreshold
import androidx.compose.animation.core.spring
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.util.fastCoerceIn
import com.codexquotatray.android.ConflatedUpdater
import com.kyant.backdrop.RuntimeShader
import com.kyant.backdrop.asComposeShader
import com.kyant.backdrop.isRuntimeShaderSupported
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

class InteractiveHighlight(
    val animationScope: CoroutineScope,
    val position: (size: Size, offset: Offset) -> Offset = { _, offset -> offset },
    private val externalProgress: () -> Float = { 0f },
    private val externalPosition: (size: Size) -> Offset = { size -> position(size, Offset.Zero) },
) {
    private val pressProgressAnimationSpec = spring(0.5f, 300f, 0.001f)
    private val positionAnimationSpec = spring(0.5f, 300f, Offset.VisibilityThreshold)

    private val pressProgressAnimation = Animatable(0f, 0.001f)
    private val positionAnimation = Animatable(Offset.Zero, Offset.VectorConverter, Offset.VisibilityThreshold)
    private val positionUpdater = ConflatedUpdater<Offset>(animationScope) { target ->
        positionAnimation.snapTo(target)
    }
    private var positionGeneration: ConflatedUpdater.Generation? = null

    private var startPosition = Offset.Zero
    val pressProgress: Float get() = pressProgressAnimation.value
    val offset: Offset get() = positionAnimation.value - startPosition

    private val shader =
        if (isRuntimeShaderSupported()) {
            RuntimeShader(
                """
uniform float2 size;
layout(color) uniform half4 color;
uniform float radius;
uniform float2 position;

half4 main(float2 coord) {
    float dist = distance(coord, position);
    float intensity = smoothstep(radius, radius * 0.5, dist);
    return color * intensity;
}""",
            )
        } else {
            null
        }

    val modifier: Modifier =
        Modifier.drawWithContent {
            val directProgress = pressProgressAnimation.value.fastCoerceIn(0f, 1f)
            val externalProgressValue = externalProgress().fastCoerceIn(0f, 1f)
            if (externalProgressValue > directProgress) {
                drawInteractiveHighlight(
                    progress = externalProgressValue,
                    highlightPosition = externalPosition(size),
                )
            } else {
                drawInteractiveHighlight(
                    progress = directProgress,
                    highlightPosition = position(size, positionAnimation.value),
                )
            }

            drawContent()
        }

    val gestureModifier: Modifier =
        Modifier.pointerInput(animationScope) {
            inspectDragGestures(
                onDragStart = { down ->
                    positionGeneration = positionUpdater.beginGeneration()
                    startPosition = down.position
                    animationScope.launch {
                        launch { pressProgressAnimation.animateTo(1f, pressProgressAnimationSpec) }
                        launch { positionAnimation.snapTo(startPosition) }
                    }
                },
                onDragEnd = {
                    invalidatePositionUpdates()
                    animationScope.launch {
                        launch { pressProgressAnimation.animateTo(0f, pressProgressAnimationSpec) }
                        launch { positionAnimation.animateTo(startPosition, positionAnimationSpec) }
                    }
                },
                onDragCancel = {
                    invalidatePositionUpdates()
                    animationScope.launch {
                        launch { pressProgressAnimation.animateTo(0f, pressProgressAnimationSpec) }
                        launch { positionAnimation.animateTo(startPosition, positionAnimationSpec) }
                    }
                },
            ) { change, _ ->
                positionGeneration?.let { generation ->
                    positionUpdater.submit(generation, change.position)
                }
            }
        }

    private fun invalidatePositionUpdates() {
        positionGeneration?.let(positionUpdater::invalidate)
        positionGeneration = null
    }

    private fun DrawScope.drawInteractiveHighlight(progress: Float, highlightPosition: Offset) {
        if (progress <= 0f) return

        if (shader != null) {
            drawRect(
                Color.White.copy(0.08f * progress),
                blendMode = BlendMode.Plus,
            )
            shader.apply {
                setFloatUniform("size", size.width, size.height)
                setColorUniform("color", Color.White.copy(0.15f * progress))
                setFloatUniform("radius", size.minDimension * 1.5f)
                setFloatUniform(
                    "position",
                    highlightPosition.x.fastCoerceIn(0f, size.width),
                    highlightPosition.y.fastCoerceIn(0f, size.height),
                )
            }
            drawRect(
                ShaderBrush(shader.asComposeShader()),
                blendMode = BlendMode.Plus,
            )
        } else {
            drawRect(
                Color.White.copy(0.25f * progress),
                blendMode = BlendMode.Plus,
            )
        }
    }
}

internal class InteractiveHighlightHandoff(
    private val animationScope: CoroutineScope,
) {
    private enum class Phase {
        INACTIVE,
        TRANSITIONING,
        ACTIVE,
        FADING,
    }

    private val phase = mutableStateOf(Phase.INACTIVE)
    private val transitionProgress = Animatable(0f, 0.001f)
    private val transitionStartProgress = mutableFloatStateOf(0f)
    private val fadeStartProgress = mutableFloatStateOf(0f)
    private val fadeAnimationReady = mutableStateOf(false)
    private val transitionAnimationSpec = spring(0.5f, 300f, 0.001f)
    private val fadeAnimationSpec = spring(0.5f, 300f, 0.001f)
    private var generation = 0

    val isActive: Boolean
        get() = phase.value == Phase.TRANSITIONING || phase.value == Phase.ACTIVE

    fun progress(targetProgress: Float): Float {
        val target = targetProgress.coerceIn(0f, 1f)
        return when (phase.value) {
            Phase.TRANSITIONING -> transitionStartProgress.floatValue
            Phase.ACTIVE -> {
                val transition = transitionProgress.value.coerceIn(0f, 1f)
                (transitionStartProgress.floatValue +
                    (target - transitionStartProgress.floatValue) * transition)
                    .coerceIn(0f, 1f)
            }
            Phase.FADING -> {
                if (fadeAnimationReady.value) transitionProgress.value else fadeStartProgress.floatValue
            }
            Phase.INACTIVE -> 0f
        }
    }

    fun begin(initialProgress: Float) {
        val beginGeneration = ++generation
        transitionStartProgress.floatValue = initialProgress.coerceIn(0f, 1f)
        phase.value = Phase.TRANSITIONING
        fadeAnimationReady.value = false

        animationScope.launch {
            if (generation != beginGeneration) return@launch

            try {
                transitionProgress.snapTo(0f)
                if (generation != beginGeneration) return@launch
                phase.value = Phase.ACTIVE
                transitionProgress.animateTo(1f, transitionAnimationSpec)
            } finally {
                if (generation == beginGeneration && phase.value == Phase.ACTIVE) {
                    transitionProgress.snapTo(1f)
                }
            }
        }
    }

    fun invalidate() {
        generation += 1
        phase.value = Phase.INACTIVE
        fadeAnimationReady.value = false
    }

    fun finish(currentProgress: Float) {
        if (!isActive) return

        startFade(currentProgress)
    }

    fun fadeFrom(currentProgress: Float) {
        if (isActive) {
            finish(currentProgress)
            return
        }

        startFade(currentProgress)
    }

    private fun startFade(currentProgress: Float) {
        val finishGeneration = ++generation
        val startProgress = currentProgress.coerceIn(0f, 1f)
        fadeStartProgress.floatValue = startProgress
        fadeAnimationReady.value = false
        phase.value = Phase.FADING

        animationScope.launch {
            if (generation != finishGeneration) return@launch

            try {
                transitionProgress.snapTo(startProgress)
                if (generation != finishGeneration) return@launch
                fadeAnimationReady.value = true
                transitionProgress.animateTo(0f, fadeAnimationSpec)
            } finally {
                if (generation == finishGeneration) {
                    phase.value = Phase.INACTIVE
                    fadeAnimationReady.value = false
                }
            }
        }
    }
}
