package com.codexquotatray.android

import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSummary
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TokenUsagePageStateTest {
    @Test
    fun newerPairingSyncSnapshotWinsOverAnEarlierForegroundFailure() {
        assertTrue(hasNewerTokenUsageSnapshot(snapshotAtStart = null, latestSnapshot = snapshot("12:01:00Z")))
        assertTrue(hasNewerTokenUsageSnapshot(snapshot("12:00:00Z"), snapshot("12:01:00Z")))
    }

    @Test
    fun unchangedOrMissingCacheDoesNotHideTheCurrentSyncFailure() {
        val existing = snapshot("12:00:00Z")
        assertFalse(hasNewerTokenUsageSnapshot(existing, existing))
        assertFalse(hasNewerTokenUsageSnapshot(existing, latestSnapshot = null))
    }

    private fun snapshot(time: String) = TokenUsageSnapshot(
        schemaVersion = 1,
        generatedAtUtc = "2026-08-11T$time",
        sourceTimeZone = "UTC",
        summary = TokenUsageSummary(0, 0, 0, 0, 0, null, 0, 0, 0),
        days = emptyList(),
    )
}
