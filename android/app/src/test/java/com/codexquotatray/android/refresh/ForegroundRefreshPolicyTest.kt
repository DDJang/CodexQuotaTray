package com.codexquotatray.android.refresh

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ForegroundRefreshPolicyTest {
    private val nowMillis = 1_000_000L

    @Test
    fun foregroundRefreshesWithoutAttemptOrAtTwoMinutesButNotBefore() {
        assertTrue(ForegroundRefreshPolicy.shouldRunOnForeground(true, null, nowMillis))
        assertFalse(
            ForegroundRefreshPolicy.shouldRunOnForeground(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS + 1,
                nowMillis,
            ),
        )
        assertTrue(
            ForegroundRefreshPolicy.shouldRunOnForeground(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS,
                nowMillis,
            ),
        )
    }

    @Test
    fun failureAttemptUsesTheSameTwoMinuteFreshnessRule() {
        assertFalse(
            ForegroundRefreshPolicy.shouldRunOnForeground(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS + 1,
                nowMillis,
            ),
        )
        assertTrue(
            ForegroundRefreshPolicy.shouldRunOnForeground(
                true,
                nowMillis - ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS,
                nowMillis,
            ),
        )
    }

    @Test
    fun disabledForegroundRefreshDoesNotRunButManualRefreshAlwaysCan() {
        assertFalse(ForegroundRefreshPolicy.shouldRunOnForeground(false, null, nowMillis))
        assertTrue(ForegroundRefreshPolicy.shouldRunManually())
    }
}
