package com.codexquotatray.android

import android.graphics.RenderEffect
import android.graphics.RuntimeShader
import android.graphics.Shader
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
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.asComposeRenderEffect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop

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
    modifier: Modifier = Modifier,
) {
    val density = LocalDensity.current
    val thresholdPx = with(density) { 4.dp.roundToPx() }
    val blurAlpha by animateFloatAsState(
        targetValue = if (scrollState.value > thresholdPx) 1f else 0f,
        animationSpec = tween(durationMillis = 200),
        label = "settings-header-blur",
    )
    val statusBarHeight = WindowInsets.statusBars.asPaddingValues().calculateTopPadding()
    val overlayHeight = statusBarHeight + 116.dp

    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        RuntimeGradientBlur(
            backdrop = backdrop,
            blurAlpha = blurAlpha,
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
                        0f to Color.Black.copy(alpha = 0.92f),
                        0.5f to Color.Black.copy(alpha = 0.58f),
                        1f to Color.Transparent,
                    ),
                ),
        )
    }
}

@Composable
private fun RuntimeGradientBlur(
    backdrop: Backdrop,
    blurAlpha: Float,
    modifier: Modifier,
) {
    val density = LocalDensity.current
    var measuredSize by remember { mutableStateOf(IntSize.Zero) }
    val shader = remember { RuntimeShader(GRADIENT_BLUR_SHADER) }
    val currentAlpha by rememberUpdatedState(blurAlpha)
    val renderEffect = remember(measuredSize, density) {
        if (measuredSize == IntSize.Zero) {
            null
        } else {
            shader.setFloatUniform(
                "size",
                measuredSize.width.toFloat(),
                measuredSize.height.toFloat(),
            )
            shader.setColorUniform("tint", android.graphics.Color.BLACK)
            shader.setFloatUniform("tintIntensity", 0.35f)
            val gradientMask = RenderEffect.createRuntimeShaderEffect(shader, "content")
            val radius = with(density) { 16.dp.toPx() }
            val blur = RenderEffect.createBlurEffect(radius, radius, Shader.TileMode.CLAMP)
            RenderEffect.createChainEffect(gradientMask, blur).asComposeRenderEffect()
        }
    }

    Box(
        modifier
            .onSizeChanged { measuredSize = it }
            .alpha(currentAlpha)
            .graphicsLayer {
                compositingStrategy = CompositingStrategy.Offscreen
                this.renderEffect = renderEffect
            }
            .drawBackdrop(
                backdrop = backdrop,
                shape = { RectangleShape },
                effects = {},
                onDrawSurface = {},
            ),
    )
}
