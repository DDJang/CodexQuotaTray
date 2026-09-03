// Adapted and modified from Kyant0/AndroidLiquidGlass.
// Pinned commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
package com.codexquotatray.android.liquidglass

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.VisibilityThreshold
import androidx.compose.animation.core.spring
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ShaderBrush
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
) {
    private val pressProgressAnimationSpec = spring(0.5f, 300f, 0.001f)
    private val positionAnimationSpec = spring(0.5f, 300f, Offset.VisibilityThreshold)

    private val pressProgressAnimation = Animatable(0f, 0.001f)
    private val positionAnimation = Animatable(Offset.Zero, Offset.VectorConverter, Offset.VisibilityThreshold)
    private val positionUpdater = ConflatedUpdater<Offset>(animationScope) { target ->
        positionAnimation.snapTo(target)
    }
    private var positionGeneration: ConflatedUpdater.Generation? = null
    private var usesExternalPosition = false
    private val visible = mutableStateOf(false)

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
            val progress = if (visible.value) pressProgressAnimation.value else 0f
            if (progress > 0f) {
                if (shader != null) {
                    drawRect(
                        Color.White.copy(0.08f * progress),
                        blendMode = BlendMode.Plus,
                    )
                    shader.apply {
                        val position = if (usesExternalPosition) {
                            positionAnimation.value
                        } else {
                            position(size, positionAnimation.value)
                        }
                        setFloatUniform("size", size.width, size.height)
                        setColorUniform("color", Color.White.copy(0.15f * progress))
                        setFloatUniform("radius", size.minDimension * 1.5f)
                        setFloatUniform(
                            "position",
                            position.x.fastCoerceIn(0f, size.width),
                            position.y.fastCoerceIn(0f, size.height),
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

            drawContent()
        }

    val gestureModifier: Modifier =
        Modifier.pointerInput(animationScope) {
            inspectDragGestures(
                onDragStart = { down ->
                    begin(
                        down.position,
                        externalPosition = false,
                        visibleImmediately = true,
                    )
                },
                onDragEnd = { release() },
                onDragCancel = { cancel() },
            ) { change, _ ->
                moveTo(change.position)
            }
    }

    fun pressAt(position: Offset) {
        prepareAt(position)
        reveal()
    }

    fun prepareAt(position: Offset) {
        begin(
            position,
            externalPosition = true,
            visibleImmediately = false,
        )
    }

    fun reveal() {
        visible.value = true
    }

    fun cancelPrepared() {
        visible.value = false
        invalidatePositionUpdates()
        animationScope.launch {
            pressProgressAnimation.snapTo(0f)
        }
    }

    fun moveTo(position: Offset) {
        positionGeneration?.let { generation ->
            positionUpdater.submit(generation, position)
        }
    }

    fun release() {
        end()
    }

    fun cancel() {
        end()
    }

    private fun begin(
        position: Offset,
        externalPosition: Boolean,
        visibleImmediately: Boolean,
    ) {
        usesExternalPosition = externalPosition
        visible.value = visibleImmediately
        positionGeneration = positionUpdater.beginGeneration()
        startPosition = position
        animationScope.launch {
            launch { pressProgressAnimation.animateTo(1f, pressProgressAnimationSpec) }
            launch { positionAnimation.snapTo(startPosition) }
        }
    }

    private fun end() {
        invalidatePositionUpdates()
        animationScope.launch {
            launch { positionAnimation.animateTo(startPosition, positionAnimationSpec) }
            pressProgressAnimation.animateTo(0f, pressProgressAnimationSpec)
            visible.value = false
        }
    }

    private fun invalidatePositionUpdates() {
        positionGeneration?.let(positionUpdater::invalidate)
        positionGeneration = null
    }
}
