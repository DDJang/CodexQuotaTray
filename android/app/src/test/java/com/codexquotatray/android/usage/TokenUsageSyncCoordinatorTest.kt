package com.codexquotatray.android.usage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate

class TokenUsageSyncCoordinatorTest {
    @Test
    fun successfulSyncCommitsCacheBeforePairingAndThenPublishesEvent() {
        val calls = mutableListOf<String>()
        val pairingStore = MemoryPairingStore(calls)
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing ->
                calls += "sync"
                TokenUsageSyncResult(snapshot(), pairing)
            },
            cache = TokenUsageCacheStore {
                calls += "cache"
                true
            },
            pairingStore = pairingStore,
            notifyCompleted = { calls += "event" },
        )

        val result = coordinator.sync(pairing())

        assertEquals(listOf("sync", "cache", "pairing", "event"), calls)
        assertTrue(result.pairing.lastSuccessfulSyncAtMillis != null)
        assertEquals(result.pairing, pairingStore.saved)
    }

    @Test
    fun cacheFailureCannotPersistSuccessfulSyncTimestampOrPublishEvent() {
        val calls = mutableListOf<String>()
        val pairingStore = MemoryPairingStore(calls)
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing ->
                calls += "sync"
                TokenUsageSyncResult(snapshot(), pairing)
            },
            cache = TokenUsageCacheStore {
                calls += "cache"
                false
            },
            pairingStore = pairingStore,
            notifyCompleted = { calls += "event" },
        )

        assertThrows(TokenUsageCommitException::class.java) { coordinator.sync(pairing()) }

        assertEquals(listOf("sync", "cache"), calls)
        assertFalse(pairingStore.saved?.lastSuccessfulSyncAtMillis != null)
    }

    private class MemoryPairingStore(private val calls: MutableList<String>) : TokenSyncPairingStore {
        var saved: TokenSyncPairing? = null
        override fun load(): TokenSyncPairing? = saved
        override fun save(pairing: TokenSyncPairing): Boolean {
            calls += "pairing"
            saved = pairing
            return true
        }
    }

    private fun pairing() = TokenSyncEndpoint.validated(
        "123e4567-e89b-12d3-a456-426614174000",
        "192.168.1.10",
        43821,
        "secret",
    )

    private fun snapshot() = TokenUsageSnapshot(
        schemaVersion = 1,
        generatedAtUtc = "2026-08-10T12:00:00Z",
        sourceTimeZone = "Asia/Shanghai",
        summary = TokenUsageSummary(1, 1, 1, 1, 1, LocalDate.of(2026, 8, 10), 1, 1, 1),
        days = emptyList(),
    )
}
