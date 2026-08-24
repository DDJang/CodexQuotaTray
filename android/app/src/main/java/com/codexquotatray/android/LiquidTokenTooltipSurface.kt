package com.codexquotatray.android

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.colorControls
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.highlight.Highlight
import com.kyant.shapes.RoundedRectangle

/**
 * Kyant Dialog optical pipeline, fixed to the upstream reference commit used
 * by the Liquid Token Tooltip Fixture.
 */
@Composable
internal fun liquidTokenDialogSurfaceModifier(
    modifier: Modifier,
    backdrop: Backdrop,
    isDark: Boolean,
): Modifier {
    val containerColor = if (isDark) {
        Color(0xFF121212).copy(alpha = 0.4f)
    } else {
        Color(0xFFFAFAFA).copy(alpha = 0.6f)
    }
    return modifier.drawBackdrop(
        backdrop = backdrop,
        shape = { RoundedRectangle(16.dp) },
        effects = {
            colorControls(
                brightness = if (isDark) 0f else 0.2f,
                saturation = 1.5f,
                contrast = 1f,
            )
            blur(if (isDark) 8.dp.toPx() else 16.dp.toPx())
            lens(24.dp.toPx(), 48.dp.toPx(), depthEffect = true)
        },
        highlight = { Highlight.Plain },
        onDrawSurface = { drawRect(containerColor) },
    )
}
