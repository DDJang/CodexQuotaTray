package com.codexquotatray.android

import com.codexquotatray.android.usage.TokenSyncEndpoint
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSummary
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TokenPairingReconcileTest {
    @Test fun unpairedReconcileClearsPairedStateAndSnapshot() {
        val decision = tokenPairingReconcileDecision(pairingA(), null, snapshot("2026-08-10T12:00:00Z"), null)

        assertTrue(decision.unpaired)
        assertTrue(decision.pairingChanged)
        assertNull(decision.snapshotToDisplay)
    }

    @Test fun pairingReplacementNeverKeepsOldComputerSnapshot() {
        val decision = tokenPairingReconcileDecision(
            previousPairing = pairingA(),
            currentPairing = pairingB(),
            currentSnapshot = snapshot("2026-08-10T12:00:00Z"),
            cachedSnapshot = null,
        )

        assertTrue(decision.pairingChanged)
        assertFalse(decision.unpaired)
        assertNull(decision.snapshotToDisplay)
    }

    @Test fun newPairingCacheIsDisplayedImmediately() {
        val cached = snapshot("2026-08-11T12:00:00Z")
        val decision = tokenPairingReconcileDecision(pairingA(), pairingB(), snapshot("2026-08-10T12:00:00Z"), cached)

        assertEquals(cached, decision.snapshotToDisplay)
    }

    @Test fun samePairingFailureStateWinsOverSameOldCache() {
        val old = snapshot("2026-08-10T12:00:00Z")
        val decision = tokenPairingReconcileDecision(pairingA(), pairingA(), old, old.copy())

        assertFalse(decision.pairingChanged)
        assertEquals(old, decision.snapshotToDisplay)
    }

    @Test fun samePairingNewerCacheRestoresSuccessState() {
        val old = snapshot("2026-08-10T12:00:00Z")
        val newer = snapshot("2026-08-11T12:00:00Z")
        val decision = tokenPairingReconcileDecision(pairingA(), pairingA(), old, newer)

        assertEquals(newer, decision.snapshotToDisplay)
    }

    private fun pairingA(): TokenSyncPairing = TokenSyncEndpoint.validated(
        "123e4567-e89b-12d3-a456-426614174000", "192.168.1.10", 43821, "secret-a",
    )

    private fun pairingB(): TokenSyncPairing = TokenSyncEndpoint.validated(
        "123e4567-e89b-12d3-a456-426614174001", "192.168.1.11", 43821, "secret-b",
    )

    private fun snapshot(generatedAtUtc: String) = TokenUsageSnapshot(
        schemaVersion = 1,
        generatedAtUtc = generatedAtUtc,
        sourceTimeZone = "UTC",
        summary = TokenUsageSummary(0, 0, 0, 0, 0, null, 0, 0, 0),
        days = emptyList(),
    )
}
