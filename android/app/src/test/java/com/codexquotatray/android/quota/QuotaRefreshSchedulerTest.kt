package com.codexquotatray.android.quota

import androidx.work.NetworkType
import com.codexquotatray.android.refresh.BackgroundNetworkCapability
import com.codexquotatray.android.refresh.BackgroundNetworkTransport
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaRefreshSchedulerTest {
    @Test
    fun oauthOnlyRequiresValidatedInternet() {
        val requirement = QuotaRefreshScheduler.networkRequirement(
            hasOAuth = true,
            hasWindowsPairing = false,
        )

        assertEquals(emptySet<BackgroundNetworkTransport>(), requirement.transports)
        assertEquals(NetworkType.CONNECTED, requirement.fallbackNetworkType)
        assertTrue(requirement.capabilities.contains(BackgroundNetworkCapability.INTERNET))
        assertTrue(requirement.capabilities.contains(BackgroundNetworkCapability.VALIDATED))
        assertTrue(requirement.capabilities.contains(BackgroundNetworkCapability.NOT_SUSPENDED))
        assertTrue(requirement.usesNetworkRequest)
    }

    @Test
    fun windowsPairingOnlyRequiresWifiWithoutInternetCapability() {
        val requirement = QuotaRefreshScheduler.networkRequirement(
            hasOAuth = false,
            hasWindowsPairing = true,
        )

        assertEquals(setOf(BackgroundNetworkTransport.WIFI), requirement.transports)
        assertFalse(requirement.transports.contains(BackgroundNetworkTransport.CELLULAR))
        assertTrue(requirement.capabilities.contains(BackgroundNetworkCapability.NOT_SUSPENDED))
        assertFalse(requirement.capabilities.contains(BackgroundNetworkCapability.INTERNET))
        assertFalse(requirement.capabilities.contains(BackgroundNetworkCapability.VALIDATED))
        assertTrue(requirement.usesNetworkRequest)
    }

    @Test
    fun oauthAndWindowsPairingAllowsWifiOrCellularWithoutInternetCapability() {
        val requirement = QuotaRefreshScheduler.networkRequirement(
            hasOAuth = true,
            hasWindowsPairing = true,
        )

        assertEquals(
            setOf(BackgroundNetworkTransport.WIFI, BackgroundNetworkTransport.CELLULAR),
            requirement.transports,
        )
        assertTrue(requirement.capabilities.contains(BackgroundNetworkCapability.NOT_SUSPENDED))
        assertFalse(requirement.capabilities.contains(BackgroundNetworkCapability.INTERNET))
        assertFalse(requirement.capabilities.contains(BackgroundNetworkCapability.VALIDATED))
        assertTrue(requirement.usesNetworkRequest)
    }

    @Test
    fun dataSourcePresenceControlsQuotaScheduling() {
        val settings = QuotaRefreshSettings(enabled = true)

        assertTrue(QuotaRefreshScheduler.shouldSchedule(settings, hasOAuth = true, hasWindowsPairing = false))
        assertTrue(QuotaRefreshScheduler.shouldSchedule(settings, hasOAuth = false, hasWindowsPairing = true))
        assertTrue(QuotaRefreshScheduler.shouldSchedule(settings, hasOAuth = true, hasWindowsPairing = true))
        assertFalse(QuotaRefreshScheduler.shouldSchedule(settings, hasOAuth = false, hasWindowsPairing = false))
        assertFalse(
            QuotaRefreshScheduler.shouldSchedule(
                settings.copy(enabled = false),
                hasOAuth = true,
                hasWindowsPairing = true,
            ),
        )
    }

    @Test
    fun quotaTransientFailuresRetryTwiceThenWaitForNextPeriodicCycle() {
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.RETRY,
            quotaRetryDecision(QuotaReadFailureKind.NETWORK, runAttemptCount = 0),
        )
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.RETRY,
            quotaRetryDecision(QuotaReadFailureKind.SERVER, runAttemptCount = 1),
        )
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.EXHAUSTED,
            quotaRetryDecision(QuotaReadFailureKind.NETWORK, runAttemptCount = 2),
        )
        assertEquals(
            com.codexquotatray.android.refresh.BackgroundRetryDecision.PERMANENT,
            quotaRetryDecision(QuotaReadFailureKind.LOGIN_REQUIRED, runAttemptCount = 0),
        )
    }
}
