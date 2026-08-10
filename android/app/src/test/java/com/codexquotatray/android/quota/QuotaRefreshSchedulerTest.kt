package com.codexquotatray.android.quota

import androidx.work.NetworkType
import org.junit.Assert.assertEquals
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
}
