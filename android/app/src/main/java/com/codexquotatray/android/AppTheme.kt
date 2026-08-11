package com.codexquotatray.android

import android.app.Activity
import android.app.UiModeManager
import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.os.Build
import android.view.View
import android.view.Window
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color as ComposeColor
import androidx.compose.ui.graphics.toArgb
import androidx.core.view.WindowCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
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
    private val appContext = context.applicationContext
    private val preferences = appContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): ThemeMode = ThemeMode.fromStorage(preferences.getString(KEY_MODE, null))

    fun save(mode: ThemeMode) {
        // Theme changes are consumed immediately by the active Compose host.
        // Commit before that host recomposes so a palette read cannot observe the
        // previous value while SharedPreferences.apply() is still flushing.
        preferences.edit().putString(KEY_MODE, mode.storageValue).commit()
        synchronizeLaunchTheme()
    }

    /**
     * Persists the selected night-mode for a future cold-start splash. Activities
     * declare uiMode config handling, so Android updates their configuration
     * without recreating the current Compose tree.
     */
    fun synchronizeLaunchTheme() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return
        appContext.getSystemService(UiModeManager::class.java)
            ?.setApplicationNightMode(applicationNightMode(load()))
    }

    companion object {
        private const val PREFERENCES_NAME = "appearance_settings"
        private const val KEY_MODE = "theme_mode"

        internal fun applicationNightMode(mode: ThemeMode): Int = when (mode) {
            ThemeMode.LIGHT -> UiModeManager.MODE_NIGHT_NO
            ThemeMode.DARK -> UiModeManager.MODE_NIGHT_YES
            // AOSP maps AUTO to UI_MODE_NIGHT_UNDEFINED for the package,
            // removing the app-local night/notnight override.
            ThemeMode.SYSTEM -> UiModeManager.MODE_NIGHT_AUTO
        }
    }
}

internal fun systemThemeMode(uiMode: Int): ThemeMode =
    if ((uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES) {
        ThemeMode.DARK
    } else {
        ThemeMode.LIGHT
    }

internal fun resolveEffectiveThemeMode(selected: ThemeMode, uiMode: Int): ThemeMode =
    if (selected == ThemeMode.SYSTEM) systemThemeMode(uiMode) else selected

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

    fun effectiveMode(context: Context): ThemeMode = effectiveMode(context, mode(context))

    fun effectiveMode(context: Context, selected: ThemeMode): ThemeMode {
        return resolveEffectiveThemeMode(selected, context.resources.configuration.uiMode)
    }

    fun palette(context: Context, selected: ThemeMode = mode(context)): ThemePalette =
        when (effectiveMode(context, selected)) {
        ThemeMode.LIGHT -> ThemePalette(
            background = Color.rgb(248, 250, 253),
            surface = Color.WHITE,
            border = Color.rgb(226, 231, 240),
            title = Color.rgb(22, 33, 56),
            body = Color.rgb(35, 49, 75),
            secondary = Color.rgb(77, 91, 116),
            muted = Color.rgb(105, 117, 140),
            accent = Color.rgb(0, 136, 255),
            primaryButton = Color.rgb(0, 136, 255),
            onPrimary = Color.WHITE,
            secondaryButton = Color.rgb(229, 235, 246),
            secondaryButtonText = Color.rgb(35, 63, 111),
            progressTrack = Color.rgb(225, 232, 244),
            error = Color.rgb(170, 30, 30),
        )

        ThemeMode.DARK -> ThemePalette(
            background = Color.rgb(0, 0, 0),
            surface = Color.rgb(30, 37, 52),
            border = Color.rgb(63, 73, 92),
            title = Color.rgb(239, 243, 251),
            body = Color.rgb(224, 230, 242),
            secondary = Color.rgb(184, 194, 214),
            muted = Color.rgb(151, 163, 187),
            accent = Color.rgb(0, 145, 255),
            primaryButton = Color.rgb(0, 145, 255),
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
        val selected = mode(activity)
        // Backfill the platform launch mode for installations created before the
        // launch-theme synchronization was introduced. uiMode is handled in the
        // manifest, so this does not recreate the visible Activity.
        ThemeSettingsStore(activity).synchronizeLaunchTheme()
        activity.setTheme(
            if (effectiveMode(activity, selected) == ThemeMode.DARK) {
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

    /** Returns the top inset that keeps interactive content below status/cutout areas. */
    fun safeTopInset(insets: WindowInsetsCompat): Int = maxOf(
        insets.getInsets(WindowInsetsCompat.Type.statusBars()).top,
        insets.getInsets(WindowInsetsCompat.Type.displayCutout()).top,
    )

    /** Returns the bottom inset that keeps content above navigation/gesture areas. */
    fun safeBottomInset(insets: WindowInsetsCompat): Int = maxOf(
        insets.getInsets(WindowInsetsCompat.Type.navigationBars()).bottom,
        insets.getInsets(WindowInsetsCompat.Type.mandatorySystemGestures()).bottom,
    )

    /**
     * Applies a stable top-safe padding without accumulating the inset when the
     * system dispatches it more than once (common after an Activity attaches).
     */
    fun installTopSafePadding(root: View): View {
        val baseLeft = root.paddingLeft
        val baseTop = root.paddingTop
        val baseRight = root.paddingRight
        val baseBottom = root.paddingBottom
        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            view.setPadding(
                baseLeft,
                baseTop + safeTopInset(insets),
                baseRight,
                baseBottom,
            )
            insets
        }
        return root
    }

    private fun invokeEnableEdgeToEdge(window: Window): Boolean = runCatching {
        WindowCompat::class.java
            .getMethod("enableEdgeToEdge", Window::class.java)
            .invoke(null, window)
        true
    }.getOrDefault(false)
}

/**
 * Keeps a user-initiated palette change inside the existing Compose tree.
 * Recreating the Activity for an appearance preference makes the Android splash
 * briefly visible, which is perceived as a black/white flash.
 */
@Composable
fun rememberAnimatedThemePalette(target: ThemePalette): ThemePalette = ThemePalette(
    background = animateThemeColor(target.background),
    surface = animateThemeColor(target.surface),
    border = animateThemeColor(target.border),
    title = animateThemeColor(target.title),
    body = animateThemeColor(target.body),
    secondary = animateThemeColor(target.secondary),
    muted = animateThemeColor(target.muted),
    accent = animateThemeColor(target.accent),
    primaryButton = animateThemeColor(target.primaryButton),
    onPrimary = animateThemeColor(target.onPrimary),
    secondaryButton = animateThemeColor(target.secondaryButton),
    secondaryButtonText = animateThemeColor(target.secondaryButtonText),
    progressTrack = animateThemeColor(target.progressTrack),
    error = animateThemeColor(target.error),
)

@Composable
private fun animateThemeColor(target: Int): Int {
    val color by animateColorAsState(
        targetValue = ComposeColor(target),
        animationSpec = tween(durationMillis = 220),
        label = "theme-color",
    )
    return color.toArgb()
}
