package com.codexquotatray.android.refresh

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ForegroundRefreshPolicyTest {
    private val nowMillis = 1_000_000L

    @Test
    fun quotaOnVisibleRefreshesWithoutCacheOrAtTwoMinutesButNotBefore() {
        assertTrue(ForegroundRefreshPolicy.shouldRunOnVisible(true, null, nowMillis))
        assertFalse(
            ForegroundRefreshPolicy.shouldRunOnVisible(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS + 1,
                nowMillis,
            ),
        )
        assertTrue(
            ForegroundRefreshPolicy.shouldRunOnVisible(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS,
                nowMillis,
            ),
        )
    }

    @Test
    fun tokenOnVisibleUsesTheSameTwoMinuteFreshnessRule() {
        assertFalse(
            ForegroundRefreshPolicy.shouldRunOnVisible(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS + 1,
                nowMillis,
            ),
        )
        assertTrue(
            ForegroundRefreshPolicy.shouldRunOnVisible(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS,
                nowMillis,
            ),
        )
    }

    @Test
    fun disabledOpenRefreshDoesNotRunButManualRefreshAlwaysCan() {
        assertFalse(ForegroundRefreshPolicy.shouldRunOnVisible(false, null, nowMillis))
        assertTrue(ForegroundRefreshPolicy.shouldRunManually())
    }
}
