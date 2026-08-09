package com.codexquotatray.android

import android.graphics.RenderEffect
import android.graphics.RuntimeShader
import android.os.Build
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ScrollState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.statusBars
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.asComposeRenderEffect
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.unit.dp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawPlainBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.effect

private const val GRADIENT_BLUR_SHADER = """
uniform shader content;
uniform float2 size;
layout(color) uniform half4 tint;
uniform float tintIntensity;

half4 main(float2 coord) {
    float blurAlpha =
        smoothstep(size.y, size.y * 0.5, coord.y);

    float tintAlpha =
        smoothstep(size.y, size.y * 0.5, coord.y);

    return mix(
        content.eval(coord) * blurAlpha,
        tint * tintAlpha,
        tintIntensity
    );
}
"""

@Composable
internal fun SettingsGradientBlurHeader(
    backdrop: Backdrop,
    scrollState: ScrollState,
    tint: Color,
    modifier: Modifier = Modifier,
) {
    val blurAlpha by animateFloatAsState(
        targetValue = if (scrollState.value > 0) 1f else 0f,
        animationSpec = tween(durationMillis = 200),
        label = "blurAlpha",
    )
    val statusBarHeight = WindowInsets.statusBars.asPaddingValues().calculateTopPadding()
    val overlayHeight = statusBarHeight + 96.dp

    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && blurAlpha > 0f) {
        RuntimeGradientBlur(
            backdrop = backdrop,
            blurAlpha = blurAlpha,
            tint = tint,
            modifier = modifier.fillMaxWidth().height(overlayHeight),
        )
    } else {
        Box(
            modifier
                .fillMaxWidth()
                .height(overlayHeight)
                .alpha(blurAlpha)
                .background(
                    Brush.verticalGradient(
                        0f to tint.copy(alpha = 0.92f),
                        0.5f to tint.copy(alpha = 0.58f),
                        1f to tint.copy(alpha = 0f),
                    ),
                ),
        )
    }
}

@Composable
private fun RuntimeGradientBlur(
    backdrop: Backdrop,
    blurAlpha: Float,
    tint: Color,
    modifier: Modifier,
) {
    val shader = remember { RuntimeShader(GRADIENT_BLUR_SHADER) }

    Box(
        modifier
            .graphicsLayer {
                alpha = blurAlpha
            }
            .drawPlainBackdrop(
                backdrop = backdrop,
                shape = { RectangleShape },
                effects = {
                    blur(4.dp.toPx())
                    shader.setFloatUniform("size", size.width, size.height)
                    shader.setColorUniform("tint", tint.toArgb())
                    shader.setFloatUniform("tintIntensity", 0.8f)
                    effect(
                        RenderEffect
                            .createRuntimeShaderEffect(shader, "content")
                            .asComposeRenderEffect(),
                    )
                },
            ),
    )
}
