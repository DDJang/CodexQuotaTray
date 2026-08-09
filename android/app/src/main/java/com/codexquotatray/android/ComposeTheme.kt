package com.codexquotatray.android

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.LocalContentColor
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
            onPrimary = Color.White,
            primaryContainer = Color(0xFF003A63),
            onPrimaryContainer = Color.White,
            secondary = Color(0xFFAEB8C5),
            onSecondary = Color(0xFF101419),
            secondaryContainer = Color(0xFF30363E),
            onSecondaryContainer = Color(0xFFF4F6F8),
            tertiary = palette.color(palette.accent),
            onTertiary = Color.White,
            tertiaryContainer = Color(0xFF003A63),
            onTertiaryContainer = Color.White,
            background = palette.color(palette.background),
            onBackground = palette.color(palette.body),
            surface = palette.color(palette.surface),
            onSurface = palette.color(palette.body),
            surfaceVariant = Color(0xFF2A2A2A),
            onSurfaceVariant = palette.color(palette.secondary),
            surfaceContainerLowest = Color.Black,
            surfaceContainerLow = Color(0xFF181818),
            surfaceContainer = Color(0xFF232323),
            surfaceContainerHigh = Color(0xFF292929),
            surfaceContainerHighest = Color(0xFF303030),
            outline = Color(0xFF555B63),
            outlineVariant = Color(0xFF343434),
            error = palette.color(palette.error),
            onError = Color(0xFF220000),
            errorContainer = Color(0xFF4A1719),
            onErrorContainer = Color(0xFFFFDAD8),
            inverseSurface = Color(0xFFE7E9ED),
            inverseOnSurface = Color(0xFF17191C),
            inversePrimary = Color(0xFF006BB8),
            scrim = Color.Black,
            surfaceTint = palette.color(palette.accent),
        )
    } else {
        lightColorScheme(
            primary = palette.color(palette.accent),
            onPrimary = Color.White,
            primaryContainer = Color(0xFFD7EBFF),
            onPrimaryContainer = Color(0xFF002F54),
            secondary = Color(0xFF4E6475),
            onSecondary = Color.White,
            secondaryContainer = Color(0xFFDCE9F3),
            onSecondaryContainer = Color(0xFF182A36),
            tertiary = palette.color(palette.accent),
            onTertiary = Color.White,
            tertiaryContainer = Color(0xFFD7EBFF),
            onTertiaryContainer = Color(0xFF002F54),
            background = palette.color(palette.background),
            onBackground = palette.color(palette.body),
            surface = palette.color(palette.surface),
            onSurface = palette.color(palette.body),
            surfaceVariant = Color(0xFFE8EDF2),
            onSurfaceVariant = palette.color(palette.secondary),
            surfaceContainerLowest = Color.White,
            surfaceContainerLow = Color(0xFFF4F6F9),
            surfaceContainer = Color(0xFFEEF2F6),
            surfaceContainerHigh = Color(0xFFE8EDF2),
            surfaceContainerHighest = Color(0xFFE1E7ED),
            outline = Color(0xFF7A8792),
            outlineVariant = Color(0xFFD3DAE2),
            error = palette.color(palette.error),
            onError = Color.White,
            errorContainer = Color(0xFFFFDAD8),
            onErrorContainer = Color(0xFF410006),
            inverseSurface = Color(0xFF2C3035),
            inverseOnSurface = Color(0xFFF2F4F7),
            inversePrimary = Color(0xFF81C3FF),
            scrim = Color.Black,
            surfaceTint = palette.color(palette.accent),
        )
    }
    androidx.compose.runtime.CompositionLocalProvider(LocalQuotaPalette provides palette) {
        MaterialTheme(colorScheme = colors) {
            androidx.compose.runtime.CompositionLocalProvider(
                LocalContentColor provides colors.onBackground,
                content = content,
            )
        }
    }
}
