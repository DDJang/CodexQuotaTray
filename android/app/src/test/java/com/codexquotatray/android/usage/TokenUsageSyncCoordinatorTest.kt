package com.codexquotatray.android.usage

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalDate
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

class TokenUsageSyncCoordinatorTest {
    @Test
    fun successfulSyncCommitsCacheBeforePairingAndThenPublishesEvent() {
        val calls = mutableListOf<String>()
        val source = pairing()
        val pairingStore = MemoryPairingStore(calls, source)
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing ->
                calls += "sync"
                TokenUsageSyncResult(snapshot(), pairing)
            },
            cache = MemoryCache(calls),
            pairingStore = pairingStore,
            notifyCompleted = { calls += "event" },
        )

        val result = coordinator.sync(source)

        assertEquals(listOf("sync", "cache", "pairing", "event"), calls)
        assertTrue(result.pairing.lastSuccessfulSyncAtMillis != null)
        assertEquals(result.pairing, pairingStore.saved)
    }

    @Test
    fun cacheFailureCannotPersistSuccessfulSyncTimestampOrPublishEvent() {
        val calls = mutableListOf<String>()
        val source = pairing()
        val pairingStore = MemoryPairingStore(calls, source)
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing ->
                calls += "sync"
                TokenUsageSyncResult(snapshot(), pairing)
            },
            cache = MemoryCache(calls, saveResult = false),
            pairingStore = pairingStore,
            notifyCompleted = { calls += "event" },
        )

        assertThrows(TokenUsageCommitException::class.java) { coordinator.sync(source) }

        assertEquals(listOf("sync", "cache"), calls)
        assertNull(pairingStore.saved?.lastSuccessfulSyncAtMillis)
    }

    @Test
    fun clearingPairingAlsoClearsTokenCache() {
        val pairingStore = MemoryPairingStore(mutableListOf(), pairing())
        val cache = MemoryCache(mutableListOf())

        assertTrue(TokenUsagePairingLifecycle.clear(pairingStore, cache))

        assertNull(pairingStore.saved)
        assertTrue(cache.cleared)
    }

    @Test
    fun cacheClearFailureDoesNotPreventPairingCredentialsRemoval() {
        val pairingStore = MemoryPairingStore(mutableListOf(), pairing())
        val cache = MemoryCache(mutableListOf(), clearResult = false)

        assertTrue(TokenUsagePairingLifecycle.clear(pairingStore, cache))

        assertNull(pairingStore.saved)
        assertTrue(cache.cleared)
    }

    @Test
    fun syncThatWasUnpairedInFlightDoesNotCommitOrRestoreOldPairing() {
        val source = pairing()
        val pairingStore = MemoryPairingStore(mutableListOf(), source)
        val cache = MemoryCache(mutableListOf())
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing ->
                pairingStore.saved = null
                TokenUsageSyncResult(snapshot(), pairing)
            },
            cache = cache,
            pairingStore = pairingStore,
        )

        assertThrows(TokenUsagePairingChangedException::class.java) { coordinator.sync(source) }

        assertNull(pairingStore.saved)
        assertNull(cache.saved)
    }

    @Test
    fun syncThatWasRepairedToAnotherDeviceDoesNotCommitOldDeviceResult() {
        val source = pairing()
        val replacement = pairing("123e4567-e89b-12d3-a456-426614174001")
        val pairingStore = MemoryPairingStore(mutableListOf(), source)
        val cache = MemoryCache(mutableListOf())
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing ->
                pairingStore.saved = replacement
                TokenUsageSyncResult(snapshot(), pairing)
            },
            cache = cache,
            pairingStore = pairingStore,
        )

        assertThrows(TokenUsagePairingChangedException::class.java) { coordinator.sync(source) }

        assertEquals(replacement, pairingStore.saved)
        assertNull(cache.saved)
    }

    @Test
    fun localSaveFailureIsNotReportedAsWindowsOffline() {
        assertEquals("Token 同步数据保存失败", tokenUsageSyncErrorMessage(TokenUsageCommitException()))
        assertEquals(
            "Windows 当前不可用",
            tokenUsageSyncErrorMessage(TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")),
        )
    }

    @Test
    fun pairingStoreFailureIsReportedAsLocalSaveFailure() {
        val source = pairing()
        val coordinator = TokenUsageSyncCoordinator(
            transport = TokenUsageSyncTransport { pairing -> TokenUsageSyncResult(snapshot(), pairing) },
            cache = MemoryCache(mutableListOf()),
            pairingStore = object : TokenSyncPairingStore {
                override fun load(): TokenSyncPairing? = source
                override fun save(pairing: TokenSyncPairing): Boolean = false
            },
        )

        val error = assertThrows(TokenUsageCommitException::class.java) { coordinator.sync(source) }

        assertEquals("Token 同步数据保存失败", tokenUsageSyncErrorMessage(error))
    }

    @Test
    fun differentDeviceIdsDoNotShareSingleFlightResults() {
        val deviceA = pairing()
        val deviceB = pairing("123e4567-e89b-12d3-a456-426614174001")
        val aStarted = CountDownLatch(1)
        val bStarted = CountDownLatch(1)
        val release = CountDownLatch(1)
        val transport = TokenUsageSyncTransport { pairing ->
            if (pairing.deviceId == deviceA.deviceId) aStarted.countDown() else bStarted.countDown()
            assertTrue(release.await(2, TimeUnit.SECONDS))
            TokenUsageSyncResult(snapshot(), pairing)
        }
        val coordinatorA = TokenUsageSyncCoordinator(transport, MemoryCache(mutableListOf()), MemoryPairingStore(mutableListOf(), deviceA))
        val coordinatorB = TokenUsageSyncCoordinator(transport, MemoryCache(mutableListOf()), MemoryPairingStore(mutableListOf(), deviceB))
        var failure: Throwable? = null
        val first = thread { runCatching { coordinatorA.sync(deviceA) }.onFailure { failure = it } }
        assertTrue(aStarted.await(2, TimeUnit.SECONDS))
        val second = thread { runCatching { coordinatorB.sync(deviceB) }.onFailure { failure = it } }
        assertTrue(bStarted.await(2, TimeUnit.SECONDS))

        release.countDown()
        first.join(2_000)
        second.join(2_000)

        assertFalse(first.isAlive)
        assertFalse(second.isAlive)
        assertNull(failure)
    }

    @Test
    fun sameDeviceWithChangedSecretDoesNotShareSingleFlightResults() {
        val oldPairing = pairing(secret = "old-secret")
        val newPairing = pairing(secret = "new-secret")
        val oldStarted = CountDownLatch(1)
        val newStarted = CountDownLatch(1)
        val release = CountDownLatch(1)
        val transport = TokenUsageSyncTransport { pairing ->
            if (pairing.pairingSecret == oldPairing.pairingSecret) oldStarted.countDown() else newStarted.countDown()
            assertTrue(release.await(2, TimeUnit.SECONDS))
            TokenUsageSyncResult(snapshot(), pairing)
        }
        val oldCoordinator = TokenUsageSyncCoordinator(
            transport,
            MemoryCache(mutableListOf()),
            MemoryPairingStore(mutableListOf(), oldPairing),
        )
        val newCoordinator = TokenUsageSyncCoordinator(
            transport,
            MemoryCache(mutableListOf()),
            MemoryPairingStore(mutableListOf(), newPairing),
        )
        var failure: Throwable? = null
        val first = thread { runCatching { oldCoordinator.sync(oldPairing) }.onFailure { failure = it } }
        assertTrue(oldStarted.await(2, TimeUnit.SECONDS))
        val second = thread { runCatching { newCoordinator.sync(newPairing) }.onFailure { failure = it } }
        assertTrue(newStarted.await(2, TimeUnit.SECONDS))

        release.countDown()
        first.join(2_000)
        second.join(2_000)

        assertFalse(first.isAlive)
        assertFalse(second.isAlive)
        assertNull(failure)
    }

    private class MemoryPairingStore(
        private val calls: MutableList<String>,
        var saved: TokenSyncPairing?,
    ) : TokenSyncPairingStore {
        override fun load(): TokenSyncPairing? = saved
        override fun save(pairing: TokenSyncPairing): Boolean {
            calls += "pairing"
            saved = pairing
            return true
        }

        override fun clear(): Boolean {
            saved = null
            return true
        }
    }

    private class MemoryCache(
        private val calls: MutableList<String>,
        private val saveResult: Boolean = true,
        private val clearResult: Boolean = true,
    ) : TokenUsageCacheStore {
        var saved: TokenUsageSnapshot? = null
        var cleared = false

        override fun save(pairing: TokenSyncPairing, snapshot: TokenUsageSnapshot): Boolean {
            calls += "cache"
            if (saveResult) saved = snapshot
            return saveResult
        }

        override fun clear(): Boolean {
            cleared = true
            saved = null
            return clearResult
        }
    }

    private fun pairing(
        deviceId: String = "123e4567-e89b-12d3-a456-426614174000",
        secret: String = "secret",
    ) = TokenSyncEndpoint.validated(
        deviceId,
        "192.168.1.10",
        43821,
        secret,
    )

    private fun snapshot() = TokenUsageSnapshot(
        schemaVersion = 1,
        generatedAtUtc = "2026-08-10T12:00:00Z",
        sourceTimeZone = "Asia/Shanghai",
        summary = TokenUsageSummary(1, 1, 1, 1, 1, LocalDate.of(2026, 8, 10), 1, 1, 1),
        days = emptyList(),
    )
}
