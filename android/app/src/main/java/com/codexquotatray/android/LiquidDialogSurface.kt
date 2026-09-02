package com.codexquotatray.android

import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.unit.dp
import com.kyant.backdrop.Backdrop

@Composable
internal fun LiquidDialogSurface(
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val isDark = palette.color(palette.background).luminance() < 0.35f
    val shape = RoundedCornerShape(SettingsUiTokens.groupCornerRadius)
    Box(
        modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp),
    ) {
        GlassSurface(
            backdrop = backdrop,
            shape = shape,
            modifier = Modifier.fillMaxWidth(),
            clippedModifier = Modifier.border(
                width = 1.dp,
                color = palette.color(palette.border).copy(alpha = 0.8f),
                shape = shape,
            ),
            contentAlignment = Alignment.TopStart,
            blurRadius = 8.dp,
            refractionHeight = 12.dp,
            refractionAmount = 24.dp,
            surfaceAlpha = if (isDark) 0.46f else 0.58f,
            surfaceColor = palette.color(palette.background),
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 20.dp),
                verticalArrangement = Arrangement.spacedBy(18.dp),
                content = content,
            )
        }
    }
}
