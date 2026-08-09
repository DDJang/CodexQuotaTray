package com.codexquotatray.android

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance

internal val LocalQuotaPalette = staticCompositionLocalOf<ThemePalette> {
    error("CodexQuota Compose palette is not installed")
}

internal fun ThemePalette.color(value: Int): Color = Color(value)

@Composable
internal fun CodexQuotaTheme(
    palette: ThemePalette,
    content: @Composable () -> Unit,
) {
    val dark = isSystemInDarkTheme() || Color(palette.background).luminance() < 0.35f
    val colors = if (dark) {
        darkColorScheme(
            primary = palette.color(palette.accent),
            background = palette.color(palette.background),
            surface = palette.color(palette.surface),
            onBackground = palette.color(palette.body),
            onSurface = palette.color(palette.body),
            error = palette.color(palette.error),
        )
    } else {
        lightColorScheme(
            primary = palette.color(palette.accent),
            background = palette.color(palette.background),
            surface = palette.color(palette.surface),
            onBackground = palette.color(palette.body),
            onSurface = palette.color(palette.body),
            error = palette.color(palette.error),
        )
    }
    androidx.compose.runtime.CompositionLocalProvider(LocalQuotaPalette provides palette) {
        MaterialTheme(colorScheme = colors, content = content)
    }
}
