package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MainTabStateTest {
    @Test
    fun defaultsToQuotaAndSwitchesBetweenPagesWithoutActivityNavigation() {
        val initial = MainTabState()
        assertEquals(MainTab.QUOTA, initial.selectedTab)
        assertFalse(initial.usageHasBeenShown)

        val usage = initial.select(MainTab.USAGE)
        assertEquals(MainTab.USAGE, usage.selectedTab)
        assertTrue(usage.usageHasBeenShown)

        assertEquals(MainTab.QUOTA, usage.select(MainTab.QUOTA).selectedTab)
    }

    @Test
    fun usageAutoSyncIsOnlyNeededOnFirstShow() {
        val initial = MainTabState()
        assertTrue(initial.shouldAutoSyncUsageOnShow())

        val shown = initial.select(MainTab.USAGE)
        assertFalse(shown.shouldAutoSyncUsageOnShow())
        assertFalse(shown.select(MainTab.QUOTA).shouldAutoSyncUsageOnShow())
    }

    @Test
    fun backFromUsageReturnsToQuotaButQuotaBackHasNoTabTransition() {
        val usage = MainTabState().select(MainTab.USAGE)
        assertEquals(MainTab.QUOTA, usage.backToQuota()?.selectedTab)
        assertEquals(null, MainTabState().backToQuota())
    }

    @Test
    fun unpairedUsageUsesEmptyState() {
        assertEquals(TokenUsagePageMode.EMPTY_UNPAIRED, tokenUsagePageMode(false))
        assertEquals(TokenUsagePageMode.CONTENT, tokenUsagePageMode(true))
    }
}
