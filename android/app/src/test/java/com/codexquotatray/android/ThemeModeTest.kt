package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Test

class ThemeModeTest {
    @Test
    fun unknownStoredThemeFallsBackToSystem() {
        assertEquals(ThemeMode.SYSTEM, ThemeMode.fromStorage("unknown"))
        assertEquals(ThemeMode.SYSTEM, ThemeMode.fromStorage(null))
    }

    @Test
    fun systemThemeRoundTripsThroughStoredValue() {
        assertEquals(ThemeMode.SYSTEM, ThemeMode.fromStorage(ThemeMode.SYSTEM.storageValue))
    }

    @Test
    fun darkThemeRoundTripsThroughStoredValue() {
        assertEquals(ThemeMode.DARK, ThemeMode.fromStorage(ThemeMode.DARK.storageValue))
    }
}
