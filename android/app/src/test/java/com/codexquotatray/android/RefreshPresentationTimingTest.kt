package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Test

class RefreshPresentationTimingTest {
    @Test
    fun quickRequestKeepsPresentationVisibleForTheRemainingMinimum() {
        assertEquals(600L, remainingRefreshPresentationMillis(1_000L, 1_100L))
    }

    @Test
    fun requestAtMinimumDurationNeedsNoAdditionalDelay() {
        assertEquals(0L, remainingRefreshPresentationMillis(1_000L, 1_700L))
    }

    @Test
    fun slowRequestNeedsNoAdditionalDelay() {
        assertEquals(0L, remainingRefreshPresentationMillis(1_000L, 2_500L))
    }
}
