package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.QuotaRefreshSettings
import com.codexquotatray.android.refresh.BackgroundNetworkCapability
import com.codexquotatray.android.refresh.BackgroundNetworkTransport
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TokenUsageRefreshSettingsTest {
    @Test
    fun tokenBackgroundSchedulingAcceptsEitherOAuthOrWindowsPairing() {
        val enabled = TokenUsageRefreshSettings(backgroundSyncEnabled = true, intervalMinutes = 30)
        assertTrue(TokenUsageRefreshScheduler.shouldSchedule(enabled, hasOAuth = true, hasWindowsPairing = false))
        assertTrue(TokenUsageRefreshScheduler.shouldSchedule(enabled, hasOAuth = false, hasWindowsPairing = true))
        assertFalse(TokenUsageRefreshScheduler.shouldSchedule(enabled, hasOAuth = false, hasWindowsPairing = false))
        assertFalse(
            TokenUsageRefreshScheduler.shouldSchedule(
                enabled.copy(backgroundSyncEnabled = false),
                hasOAuth = true,
                hasWindowsPairing = true,
            ),
        )
    }

    @Test
    fun pairedLanSyncRequiresWifiWithoutValidatedInternet() {
        val requirement = TokenUsageRefreshScheduler.networkRequirement(hasOAuth = false, hasWindowsPairing = true)

        assertEquals(setOf(BackgroundNetworkTransport.WIFI), requirement.transports)
        assertTrue(requirement.capabilities.contains(BackgroundNetworkCapability.NOT_SUSPENDED))
        assertFalse(requirement.capabilities.contains(BackgroundNetworkCapability.INTERNET))
        assertFalse(requirement.capabilities.contains(BackgroundNetworkCapability.VALIDATED))
        assertTrue(requirement.usesNetworkRequest)
    }

    @Test
    fun oauthOnlyRequiresValidatedInternetAndCombinedSourcesAllowLanOnlyWifi() {
        val oauthOnly = TokenUsageRefreshScheduler.networkRequirement(hasOAuth = true, hasWindowsPairing = false)
        assertTrue(oauthOnly.capabilities.contains(BackgroundNetworkCapability.INTERNET))
        assertTrue(oauthOnly.capabilities.contains(BackgroundNetworkCapability.VALIDATED))

        val combined = TokenUsageRefreshScheduler.networkRequirement(hasOAuth = true, hasWindowsPairing = true)
        assertEquals(setOf(BackgroundNetworkTransport.WIFI, BackgroundNetworkTransport.CELLULAR), combined.transports)
        assertFalse(combined.capabilities.contains(BackgroundNetworkCapability.INTERNET))
    }

    @Test
    fun tokenAndQuotaBackgroundSettingsRemainIndependent() {
        val quotaBackgroundDisabled = QuotaRefreshSettings(enabled = false)
        val tokenBackgroundEnabled = TokenUsageRefreshSettings(backgroundSyncEnabled = true)

        assertFalse(quotaBackgroundDisabled.enabled)
        assertTrue(TokenUsageRefreshScheduler.shouldSchedule(tokenBackgroundEnabled, hasOAuth = true, hasWindowsPairing = false))
    }

    @Test
    fun tokenRefreshOptionsMatchWorkManagerMinimumAndNormalizeInvalidValues() {
        assertEquals(listOf(15, 30, 60), TokenUsageRefreshSettings.SUPPORTED_INTERVAL_MINUTES)
        assertEquals(
            TokenUsageRefreshSettings.DEFAULT_INTERVAL_MINUTES,
            TokenUsageRefreshSettings(intervalMinutes = 5).normalizedIntervalMinutes,
        )
    }

    @Test
    fun tokenTransientFailuresRetryTwiceWhilePairingFailuresArePermanent() {
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.RETRY,
            tokenRetryDecision(
                TokenUsageException(TokenUsageFailureKind.OFFLINE, "offline"),
                runAttemptCount = 0,
            ),
        )
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.EXHAUSTED,
            tokenRetryDecision(
                TokenUsageException(TokenUsageFailureKind.HTTP_ERROR, "server"),
                runAttemptCount = 2,
            ),
        )
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.PERMANENT,
            tokenRetryDecision(
                TokenUsageException(TokenUsageFailureKind.PAIRING_INVALID, "pairing"),
                runAttemptCount = 0,
            ),
        )
    }

    @Test
    fun quotaAndTokenRetryPoliciesRemainIndependent() {
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.PERMANENT,
            com.codexquotatray.android.quota.quotaRetryDecision(
                com.codexquotatray.android.quota.QuotaReadFailureKind.LOGIN_REQUIRED,
                runAttemptCount = 0,
            ),
        )
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.RETRY,
            tokenRetryDecision(
                TokenUsageException(TokenUsageFailureKind.OFFLINE, "offline"),
                runAttemptCount = 0,
            ),
        )
    }
}
