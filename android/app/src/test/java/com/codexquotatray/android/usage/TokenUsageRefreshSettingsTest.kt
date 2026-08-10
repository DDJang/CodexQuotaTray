package com.codexquotatray.android.usage

import androidx.work.NetworkType
import com.codexquotatray.android.quota.QuotaRefreshSettings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TokenUsageRefreshSettingsTest {
    @Test
    fun tokenBackgroundSchedulingRequiresBothItsOwnSettingAndAPairing() {
        val enabled = TokenUsageRefreshSettings(backgroundSyncEnabled = true, intervalMinutes = 30)
        assertTrue(TokenUsageRefreshScheduler.shouldSchedule(enabled, hasPairing = true))
        assertFalse(TokenUsageRefreshScheduler.shouldSchedule(enabled, hasPairing = false))
        assertFalse(
            TokenUsageRefreshScheduler.shouldSchedule(
                enabled.copy(backgroundSyncEnabled = false),
                hasPairing = true,
            ),
        )
    }

    @Test
    fun pairedLanSyncDoesNotRequireValidatedInternetToBeScheduled() {
        assertEquals(NetworkType.NOT_REQUIRED, TokenUsageRefreshScheduler.requiredNetworkType())
    }

    @Test
    fun tokenBackgroundWorkerSkipsWhenWifiLanIsUnavailable() {
        val enabled = TokenUsageRefreshSettings(backgroundSyncEnabled = true)

        assertFalse(
            TokenUsageRefreshScheduler.shouldRunOnWifiLan(
                settings = enabled,
                hasPairing = true,
                isWifiLanAvailable = false,
            ),
        )
        assertTrue(
            TokenUsageRefreshScheduler.shouldRunOnWifiLan(
                settings = enabled,
                hasPairing = true,
                isWifiLanAvailable = true,
            ),
        )
    }

    @Test
    fun tokenAndQuotaBackgroundSettingsRemainIndependent() {
        val quotaBackgroundDisabled = QuotaRefreshSettings(enabled = false)
        val tokenBackgroundEnabled = TokenUsageRefreshSettings(backgroundSyncEnabled = true)

        assertFalse(quotaBackgroundDisabled.enabled)
        assertTrue(TokenUsageRefreshScheduler.shouldSchedule(tokenBackgroundEnabled, hasPairing = true))
    }

    @Test
    fun tokenRefreshOptionsMatchWorkManagerMinimumAndNormalizeInvalidValues() {
        assertEquals(listOf(15, 30, 60), TokenUsageRefreshSettings.SUPPORTED_INTERVAL_MINUTES)
        assertEquals(
            TokenUsageRefreshSettings.DEFAULT_INTERVAL_MINUTES,
            TokenUsageRefreshSettings(intervalMinutes = 5).normalizedIntervalMinutes,
        )
    }
}
