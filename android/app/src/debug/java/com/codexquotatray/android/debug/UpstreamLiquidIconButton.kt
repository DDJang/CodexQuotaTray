/*
 * Adapted from Kyant0/AndroidLiquidGlass LiquidButton.
 * Copyright Kyant0 contributors, licensed under Apache-2.0.
 */
package com.codexquotatray.android.debug

import androidx.annotation.DrawableRes
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.util.fastCoerceAtMost
import androidx.compose.ui.util.lerp
import com.codexquotatray.android.liquidglass.InteractiveHighlight
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.shapes.Capsule
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.tanh

/** Debug-only icon geometry copy of Kyant0/AndroidLiquidGlass LiquidButton. */
@Composable
internal fun UpstreamLiquidIconButton(
    @DrawableRes iconRes: Int,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    buttonSize: Dp = 52.dp,
    iconSize: Dp = 24.dp,
    onClick: () -> Unit,
) {
    val animationScope = rememberCoroutineScope()
    val interactiveHighlight = remember(animationScope) {
        InteractiveHighlight(animationScope = animationScope)
    }

    Box(
        modifier
            .size(buttonSize)
            .drawBackdrop(
                backdrop = backdrop,
                shape = { Capsule() },
                effects = {
                    vibrancy()
                    blur(2f.dp.toPx())
                    lens(12f.dp.toPx(), 24f.dp.toPx())
                },
                layerBlock = {
                    val width = size.width
                    val height = size.height
                    val progress = interactiveHighlight.pressProgress
                    val scale = lerp(1f, 1f + 4f.dp.toPx() / size.height, progress)
                    val maxOffset = size.minDimension
                    val initialDerivative = 0.05f
                    val offset = interactiveHighlight.offset
                    translationX = maxOffset * tanh(initialDerivative * offset.x / maxOffset)
                    translationY = maxOffset * tanh(initialDerivative * offset.y / maxOffset)
                    val maxDragScale = 4f.dp.toPx() / size.height
                    val offsetAngle = atan2(offset.y, offset.x)
                    scaleX = scale +
                        maxDragScale * abs(cos(offsetAngle) * offset.x / size.maxDimension) *
                        (width / height).fastCoerceAtMost(1f)
                    scaleY = scale +
                        maxDragScale * abs(sin(offsetAngle) * offset.y / size.maxDimension) *
                        (height / width).fastCoerceAtMost(1f)
                },
                onDrawSurface = {},
            )
            .clickable(
                interactionSource = null,
                indication = null,
                role = Role.Button,
                onClick = onClick,
            )
            .then(interactiveHighlight.modifier)
            .then(interactiveHighlight.gestureModifier),
        contentAlignment = Alignment.Center,
    ) {
        Icon(
            painter = painterResource(iconRes),
            contentDescription = null,
            modifier = Modifier.size(iconSize),
            tint = androidx.compose.ui.graphics.Color.White,
        )
    }
}
