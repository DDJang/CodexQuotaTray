package com.codexquotatray.android.quota

import androidx.work.NetworkType
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaRefreshSchedulerTest {
    @Test
    fun pairedWindowsUsesNoValidatedInternetConstraintSoLanOnlyFallbackCanRun() {
        assertEquals(
            NetworkType.NOT_REQUIRED,
            QuotaRefreshScheduler.requiredNetworkType(hasWindowsPairing = true),
        )
    }

    @Test
    fun unpairedQuotaRefreshKeepsTheConnectedNetworkConstraint() {
        assertEquals(
            NetworkType.CONNECTED,
            QuotaRefreshScheduler.requiredNetworkType(hasWindowsPairing = false),
        )
    }

    @Test
    fun windowsPairingAloneKeepsQuotaBackgroundWorkScheduled() {
        val settings = QuotaRefreshSettings(enabled = true)

        assertTrue(QuotaRefreshScheduler.shouldSchedule(settings, hasOAuth = false, hasWindowsPairing = true))
        assertTrue(QuotaRefreshScheduler.shouldSchedule(settings, hasOAuth = true, hasWindowsPairing = false))
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
