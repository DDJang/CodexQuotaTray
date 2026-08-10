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
            ThemeSettingsStore.applicationNightMode(ThemeMode.LIGHT, Configuration.UI_MODE_NIGHT_YES),
        )
        assertEquals(
            UiModeManager.MODE_NIGHT_YES,
            ThemeSettingsStore.applicationNightMode(ThemeMode.DARK, Configuration.UI_MODE_NIGHT_NO),
        )
    }

    @Test
    fun systemModeUsesTheCurrentSystemThemeInsteadOfSensorAutoMode() {
        assertEquals(ThemeMode.LIGHT, systemThemeMode(Configuration.UI_MODE_NIGHT_NO))
        assertEquals(ThemeMode.DARK, systemThemeMode(Configuration.UI_MODE_NIGHT_YES))
        assertEquals(
            UiModeManager.MODE_NIGHT_NO,
            ThemeSettingsStore.applicationNightMode(ThemeMode.SYSTEM, Configuration.UI_MODE_NIGHT_NO),
        )
        assertEquals(
            UiModeManager.MODE_NIGHT_YES,
            ThemeSettingsStore.applicationNightMode(ThemeMode.SYSTEM, Configuration.UI_MODE_NIGHT_YES),
        )
    }
}
