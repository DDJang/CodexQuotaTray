package com.codexquotatray.android.refresh

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AutomaticRefreshCoordinatorTest {
    @Test
    fun failedAutomaticAttemptStillSuppressesForegroundForTwoMinutes() {
        var now = 1_000_000L
        val coordinator = AutomaticRefreshCoordinator { now }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.STARTUP, enabled = true))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
        assertFalse(
            coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.FOREGROUND, enabled = true),
        )

        now += ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS
        assertTrue(
            coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.FOREGROUND, enabled = true),
        )
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
    }

    @Test
    fun inFlightRequestsAreDeduplicatedPerChannel() {
        val coordinator = AutomaticRefreshCoordinator { 1_000_000L }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.FOREGROUND, enabled = true))
        assertFalse(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.MANUAL))
        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.MANUAL))
        coordinator.finish(AutomaticRefreshChannel.TOKEN)
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
    }

    @Test
    fun manualRefreshBypassesAutomaticFreshnessWithoutChangingLastAttempt() {
        var now = 1_000_000L
        val coordinator = AutomaticRefreshCoordinator { now }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.STARTUP, enabled = true))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
        val automaticAttempt = coordinator.lastAutomaticAttemptAtMillis(AutomaticRefreshChannel.QUOTA)
        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.MANUAL))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
        assertTrue(automaticAttempt != null)
        assertTrue(automaticAttempt == coordinator.lastAutomaticAttemptAtMillis(AutomaticRefreshChannel.QUOTA))

        now += ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS
        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.FOREGROUND, enabled = true))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
    }

    @Test
    fun scheduledAttemptAlsoSuppressesStartupAndForegroundForTwoMinutes() {
        var now = 1_000_000L
        val coordinator = AutomaticRefreshCoordinator { now }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.SCHEDULED))
        coordinator.finish(AutomaticRefreshChannel.TOKEN)
        assertFalse(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.STARTUP))
        assertFalse(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.FOREGROUND))

        now += ForegroundRefreshPolicy.FRESHNESS_WINDOW_MILLIS
        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.FOREGROUND))
        coordinator.finish(AutomaticRefreshChannel.TOKEN)
    }

    @Test
    fun workManagerRetryBypassesTwoMinuteAttemptSuppressionButKeepsInflightDeduplication() {
        val coordinator = AutomaticRefreshCoordinator { 1_000_000L }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.SCHEDULED))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.RETRY))
        assertFalse(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.RETRY))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
    }

    @Test
    fun sourceChangeBypassesAutomaticFreshnessAndDisabledSetting() {
        val coordinator = AutomaticRefreshCoordinator { 1_000_000L }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.STARTUP, enabled = true))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
        assertTrue(
            coordinator.tryStart(
                AutomaticRefreshChannel.QUOTA,
                AutomaticRefreshReason.SOURCE_CHANGED,
                enabled = false,
            ),
        )
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
    }

    @Test
    fun quotaAndTokenHaveIndependentAutomaticGates() {
        val coordinator = AutomaticRefreshCoordinator { 1_000_000L }

        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.QUOTA, AutomaticRefreshReason.FOREGROUND, enabled = true))
        assertTrue(coordinator.tryStart(AutomaticRefreshChannel.TOKEN, AutomaticRefreshReason.FOREGROUND, enabled = true))
        coordinator.finish(AutomaticRefreshChannel.QUOTA)
        coordinator.finish(AutomaticRefreshChannel.TOKEN)
        assertTrue(coordinator.lastAutomaticAttemptAtMillis(AutomaticRefreshChannel.QUOTA) != null)
        assertTrue(coordinator.lastAutomaticAttemptAtMillis(AutomaticRefreshChannel.TOKEN) != null)
    }

    @Test
    fun networkRecoveryUsesExistingInFlightDeduplication() {
        val coordinator = AutomaticRefreshCoordinator { 1_000_000L }

        assertFalse(
            coordinator.tryStart(
                AutomaticRefreshChannel.TOKEN,
                AutomaticRefreshReason.NETWORK_RESTORED,
                enabled = false,
            ),
        )
        assertTrue(
            coordinator.tryStart(
                AutomaticRefreshChannel.TOKEN,
                AutomaticRefreshReason.NETWORK_RESTORED,
                enabled = true,
            ),
        )
        assertFalse(
            coordinator.tryStart(
                AutomaticRefreshChannel.TOKEN,
                AutomaticRefreshReason.NETWORK_RESTORED,
                enabled = true,
            ),
        )
        coordinator.finish(AutomaticRefreshChannel.TOKEN)
        assertTrue(coordinator.lastAutomaticAttemptAtMillis(AutomaticRefreshChannel.TOKEN) != null)
    }
}
