package com.codexquotatray.android

import android.app.Activity
import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.view.Window
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsControllerCompat

enum class ThemeMode(val storageValue: String) {
    SYSTEM("system"),
    LIGHT("light"),
    DARK("dark");

    companion object {
        fun fromStorage(value: String?): ThemeMode = when (value) {
            SYSTEM.storageValue -> SYSTEM
            DARK.storageValue -> DARK
            LIGHT.storageValue -> LIGHT
            else -> SYSTEM
        }
    }
}

class ThemeSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): ThemeMode = ThemeMode.fromStorage(preferences.getString(KEY_MODE, null))

    fun save(mode: ThemeMode) {
        preferences.edit().putString(KEY_MODE, mode.storageValue).apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "appearance_settings"
        private const val KEY_MODE = "theme_mode"
    }
}

data class ThemePalette(
    val background: Int,
    val surface: Int,
    val border: Int,
    val title: Int,
    val body: Int,
    val secondary: Int,
    val muted: Int,
    val accent: Int,
    val primaryButton: Int,
    val onPrimary: Int,
    val secondaryButton: Int,
    val secondaryButtonText: Int,
    val progressTrack: Int,
    val error: Int,
)

object AppTheme {
    fun mode(context: Context): ThemeMode = ThemeSettingsStore(context).load()

    fun effectiveMode(context: Context): ThemeMode {
        val selected = mode(context)
        if (selected != ThemeMode.SYSTEM) return selected
        return if ((context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
            Configuration.UI_MODE_NIGHT_YES
        ) {
            ThemeMode.DARK
        } else {
            ThemeMode.LIGHT
        }
    }

    fun palette(context: Context): ThemePalette = when (effectiveMode(context)) {
        ThemeMode.LIGHT -> ThemePalette(
            background = Color.rgb(248, 250, 253),
            surface = Color.WHITE,
            border = Color.rgb(226, 231, 240),
            title = Color.rgb(22, 33, 56),
            body = Color.rgb(35, 49, 75),
            secondary = Color.rgb(77, 91, 116),
            muted = Color.rgb(105, 117, 140),
            accent = Color.rgb(28, 92, 202),
            primaryButton = Color.rgb(38, 96, 211),
            onPrimary = Color.WHITE,
            secondaryButton = Color.rgb(229, 235, 246),
            secondaryButtonText = Color.rgb(35, 63, 111),
            progressTrack = Color.rgb(225, 232, 244),
            error = Color.rgb(170, 30, 30),
        )

        ThemeMode.DARK -> ThemePalette(
            background = Color.rgb(18, 22, 32),
            surface = Color.rgb(30, 37, 52),
            border = Color.rgb(63, 73, 92),
            title = Color.rgb(239, 243, 251),
            body = Color.rgb(224, 230, 242),
            secondary = Color.rgb(184, 194, 214),
            muted = Color.rgb(151, 163, 187),
            accent = Color.rgb(111, 165, 255),
            primaryButton = Color.rgb(66, 119, 224),
            onPrimary = Color.WHITE,
            secondaryButton = Color.rgb(49, 61, 84),
            secondaryButtonText = Color.rgb(215, 226, 247),
            progressTrack = Color.rgb(67, 79, 103),
            error = Color.rgb(255, 126, 126),
        )

        ThemeMode.SYSTEM -> error("System mode must be resolved before selecting a palette")
    }

    /** Selects a matching platform base theme before an Activity creates its views. */
    fun prepare(activity: Activity) {
        activity.setTheme(
            if (effectiveMode(activity) == ThemeMode.DARK) {
                android.R.style.Theme_Material_NoActionBar
            } else {
                android.R.style.Theme_Material_Light_NoActionBar
            },
        )
    }

    fun applySystemBars(activity: Activity) {
        val window = activity.window
        val lightBars = effectiveMode(activity) == ThemeMode.LIGHT

        // core-ktx 1.13.1 is pinned because the repository targets compileSdk 35;
        // newer core releases require compileSdk 36. Invoke the newer helper when
        // it is present and retain the equivalent compatibility path otherwise.
        if (!invokeEnableEdgeToEdge(window)) {
            WindowCompat.setDecorFitsSystemWindows(window, false)
        }
        window.statusBarColor = Color.TRANSPARENT
        window.navigationBarColor = Color.TRANSPARENT

        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.Q) {
            window.isNavigationBarContrastEnforced = false
            window.isStatusBarContrastEnforced = false
        }
        val controller = WindowInsetsControllerCompat(window, window.decorView)
        controller.isAppearanceLightStatusBars = lightBars
        controller.isAppearanceLightNavigationBars = lightBars
    }

    private fun invokeEnableEdgeToEdge(window: Window): Boolean = runCatching {
        WindowCompat::class.java
            .getMethod("enableEdgeToEdge", Window::class.java)
            .invoke(null, window)
        true
    }.getOrDefault(false)
}
