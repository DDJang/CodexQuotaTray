package com.codexquotatray.android.quota

import org.junit.Assert.assertEquals
import org.junit.Test

class QuotaRefreshSettingsTest {
    @Test
    fun refreshOptionsStayWithinWorkManagerMinimum() {
        assertEquals(listOf(15, 30, 60), QuotaRefreshSettings.SUPPORTED_INTERVAL_MINUTES)
        assertEquals(
            QuotaRefreshSettings.DEFAULT_INTERVAL_MINUTES,
            QuotaRefreshSettings(intervalMinutes = 5).normalizedIntervalMinutes,
        )
    }

    @Test
    fun existingBackgroundSettingsRemainSeparateFromOpenRefreshSetting() {
        val settings = QuotaRefreshSettings(autoRefreshOnOpen = false, enabled = true, intervalMinutes = 30)

        assertEquals(false, settings.autoRefreshOnOpen)
        assertEquals(true, settings.enabled)
        assertEquals(30, settings.normalizedIntervalMinutes)
    }
}
