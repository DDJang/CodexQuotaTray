package com.codexquotatray.android

import android.app.UiModeManager
import android.content.res.Configuration
import org.junit.Assert.assertEquals
import org.junit.Test

class ThemeLaunchSyncTest {
    @Test
    fun explicitModesMapToTheirAndroidLaunchModes() {
        assertEquals(
            UiModeManager.MODE_NIGHT_NO,
            ThemeSettingsStore.applicationNightMode(ThemeMode.LIGHT),
        )
        assertEquals(
            UiModeManager.MODE_NIGHT_YES,
            ThemeSettingsStore.applicationNightMode(ThemeMode.DARK),
        )
    }

    @Test
    fun systemModeClearsPackageOverrideAndUsesCurrentConfiguration() {
        assertEquals(UiModeManager.MODE_NIGHT_AUTO, ThemeSettingsStore.applicationNightMode(ThemeMode.SYSTEM))
        assertEquals(
            ThemeMode.DARK,
            resolveEffectiveThemeMode(ThemeMode.SYSTEM, Configuration.UI_MODE_NIGHT_YES),
        )
        assertEquals(
            ThemeMode.LIGHT,
            resolveEffectiveThemeMode(ThemeMode.SYSTEM, Configuration.UI_MODE_NIGHT_NO),
        )
    }

    @Test
    fun fixedModesIgnoreSystemConfigurationChanges() {
        assertEquals(
            ThemeMode.LIGHT,
            resolveEffectiveThemeMode(ThemeMode.LIGHT, Configuration.UI_MODE_NIGHT_YES),
        )
        assertEquals(
            ThemeMode.DARK,
            resolveEffectiveThemeMode(ThemeMode.DARK, Configuration.UI_MODE_NIGHT_NO),
        )
    }
}
