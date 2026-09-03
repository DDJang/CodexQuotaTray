package com.codexquotatray.android

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.unit.dp

@Composable
internal fun DashboardCardSurface(
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val cardShape = RoundedCornerShape(18.dp)
    val dark = palette.color(palette.background).luminance() < 0.1f
    val cardBrush = if (dark) {
        Brush.linearGradient(
            listOf(
                Color(0xFF2A3037).copy(alpha = 0.68f),
                Color(0xFF17191D).copy(alpha = 0.94f),
                Color(0xFF101216).copy(alpha = 0.97f),
            ),
        )
    } else {
        Brush.linearGradient(
            listOf(
                Color.White.copy(alpha = 0.96f),
                palette.color(palette.surface).copy(alpha = 0.9f),
                palette.color(palette.surface).copy(alpha = 0.98f),
            ),
        )
    }
    val darkBorder = if (dark) {
        Color.Black.copy(alpha = 0.20f)
    } else {
        palette.color(palette.border).copy(alpha = 0.44f)
    }
    val topLeftBorder = if (dark) {
        Color.White.copy(alpha = 0.25f)
    } else {
        palette.color(palette.border).copy(alpha = 0.78f)
    }
    val bottomRightBorder = if (dark) {
        Color.White.copy(alpha = 0.17f)
    } else {
        palette.color(palette.border).copy(alpha = 0.64f)
    }
    val borderBrush = Brush.sweepGradient(
        colorStops = arrayOf(
            0.00f to darkBorder,
            0.05f to bottomRightBorder,
            0.12f to bottomRightBorder,
            0.18f to darkBorder,
            0.50f to darkBorder,
            0.55f to topLeftBorder,
            0.62f to topLeftBorder,
            0.68f to darkBorder,
            1.00f to darkBorder,
        ),
    )
    Box(
        modifier
            .fillMaxWidth()
            .background(cardBrush, cardShape)
            .border(1.dp, borderBrush, cardShape)
            .padding(16.dp),
    ) {
        content()
    }
}
