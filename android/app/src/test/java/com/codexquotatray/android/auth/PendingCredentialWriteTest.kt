package com.codexquotatray.android.auth

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.Callable
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class PendingCredentialWriteTest {
    @Test
    fun failedCommitClearsOldDiskAndRetriesOnlyRotatedCredential() {
        for (clearSucceeds in listOf(true, false)) {
            val pending = PendingCredentialWrite()
            val original = OAuthCredentials("old-access", "old-refresh")
            val rotated = OAuthCredentials("new-access", "new-refresh")
            var disk: OAuthCredentials? = original
            val logs = mutableListOf<String>()
            val attempted = mutableListOf<OAuthCredentials>()
            val failure = runCatching {
                pending.save(rotated, { attempted += it; false }, {
                    if (clearSucceeds) disk = null
                    clearSucceeds
                }, logs::add)
            }.exceptionOrNull()
            assertTrue(failure is CredentialPersistenceException)
            assertTrue(pending.hasPending)
            if (clearSucceeds) assertNull(disk)
            assertTrue(runCatching {
                pending.retry({ attempted += it; false }, { clearSucceeds }, logs::add)
            }.exceptionOrNull() is CredentialPersistenceException)
            assertTrue(pending.hasPending)
            val retry = pending.retry({ attempted += it; disk = it; true }, { error("unexpected clear") }, logs::add)
            assertEquals(rotated, retry)
            assertEquals(rotated, disk)
            assertFalse(pending.hasPending)
            assertEquals(listOf(rotated, rotated, rotated), attempted)
            assertTrue(logs.any { "persisted=false" in it })
            assertTrue(logs.any { "persisted=true" in it })
            assertTrue(logs.none { "old-refresh" in it || "new-refresh" in it })
        }
    }

    @Test
    fun logoutDiscardsPendingCredentialWithoutLateWriteBack() {
        val pending = PendingCredentialWrite()
        runCatching {
            pending.save(OAuthCredentials("new-access", "new-refresh"), { false }, { true }, {})
        }
        pending.clear()
        assertFalse(pending.hasPending)
        assertNull(pending.retry({ fail("Logged out token must not be saved"); true }, { true }, {}))
    }

    @Test
    fun partialMemoryCommitMustFailAllConcurrentWaitersWithoutOldTokenRefresh() {
        val monitor = Any()
        val coordinator = CredentialRefreshCoordinator(monitor)
        val original = OAuthCredentials("old-access", "old-refresh")
        val rotated = OAuthCredentials("new-access", "new-refresh")
        var current = original
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        val joined = CountDownLatch(1)
        val executor = Executors.newFixedThreadPool(2)
        var calls = 0
        val loads = AtomicInteger()
        val task = Callable {
            runCatching {
                coordinator.refresh(original, 0, { 0 }, {
                    if (loads.incrementAndGet() == 2) joined.countDown()
                    current
                }, {
                    calls++
                    entered.countDown()
                    assertTrue(release.await(5, TimeUnit.SECONDS))
                    rotated
                }, {
                    current = it // SharedPreferences can update memory even when commit returns false.
                    throw CredentialPersistenceException()
                }, { _, _ -> fail("Persistence failure must not enter auth failure recovery") })
            }.exceptionOrNull()
        }
        try {
            val first = executor.submit(task)
            assertTrue(entered.await(5, TimeUnit.SECONDS))
            val second = executor.submit(task)
            assertTrue(joined.await(5, TimeUnit.SECONDS))
            // The second caller is under the monitor until it has joined the flight.
            synchronized(monitor) { release.countDown() }
            assertTrue(first.get(5, TimeUnit.SECONDS) is CredentialPersistenceException)
            assertTrue(second.get(5, TimeUnit.SECONDS) is CredentialPersistenceException)
            assertEquals(1, calls)
            assertEquals(rotated, current)
        } finally {
            release.countDown()
            executor.shutdownNow()
        }
    }
}
