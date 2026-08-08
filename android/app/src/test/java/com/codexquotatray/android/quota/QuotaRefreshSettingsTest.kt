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
}
