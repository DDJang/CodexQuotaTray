package com.codexquotatray.android.auth

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.Callable
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

class CredentialRefreshCoordinatorTest {
    @Test
    fun concurrentRefreshRunsOnceAndBothCallersUseLatestCredential() {
        val coordinator = CredentialRefreshCoordinator(Any())
        val original = credentials("access-a", "refresh-a")
        val refreshed = credentials("access-b", "refresh-b")
        val current = AtomicReference<OAuthCredentials?>(original)
        val generation = AtomicLong(0L)
        val refreshCalls = AtomicInteger(0)
        val enteredNetwork = CountDownLatch(1)
        val releaseNetwork = CountDownLatch(1)
        val executor = Executors.newFixedThreadPool(2)

        val task = Callable {
            coordinator.refresh(
                observed = original,
                observedGeneration = 0L,
                currentGeneration = generation::get,
                loadCurrent = current::get,
                performRefresh = {
                    refreshCalls.incrementAndGet()
                    enteredNetwork.countDown()
                    assertTrue(releaseNetwork.await(5, TimeUnit.SECONDS))
                    refreshed
                },
                saveRefreshed = { current.set(it) },
                onFailure = { _, _ -> },
            )
        }

        val first = executor.submit(task)
        assertTrue(enteredNetwork.await(5, TimeUnit.SECONDS))
        val second = executor.submit(task)
        releaseNetwork.countDown()

        assertEquals(refreshed, first.get(5, TimeUnit.SECONDS))
        assertEquals(refreshed, second.get(5, TimeUnit.SECONDS))
        assertEquals(1, refreshCalls.get())
        executor.shutdownNow()
    }

    @Test
    fun slowRefreshDoesNotHoldProcessMonitor() {
        val monitor = Any()
        val coordinator = CredentialRefreshCoordinator(monitor)
        val original = credentials("access-a", "refresh-a")
        val refreshed = credentials("access-b", "refresh-b")
        val current = AtomicReference<OAuthCredentials?>(original)
        val enteredNetwork = CountDownLatch(1)
        val releaseNetwork = CountDownLatch(1)
        val executor = Executors.newFixedThreadPool(2)

        val refresh = executor.submit(Callable {
            coordinator.refresh(
                observed = original,
                observedGeneration = 0L,
                currentGeneration = { 0L },
                loadCurrent = current::get,
                performRefresh = {
                    enteredNetwork.countDown()
                    assertTrue(releaseNetwork.await(5, TimeUnit.SECONDS))
                    refreshed
                },
                saveRefreshed = { current.set(it) },
                onFailure = { _, _ -> },
            )
        })
        assertTrue(enteredNetwork.await(5, TimeUnit.SECONDS))

        val lockProbe = executor.submit(Callable { synchronized(monitor) { true } })
        assertTrue(lockProbe.get(1, TimeUnit.SECONDS))
        releaseNetwork.countDown()
        assertEquals(refreshed, refresh.get(5, TimeUnit.SECONDS))
        executor.shutdownNow()
    }

    @Test
    fun staleRefreshDoesNotClearCredentialUpdatedByAnotherOperation() {
        val coordinator = CredentialRefreshCoordinator(Any())
        val original = credentials("access-a", "refresh-a")
        val newer = credentials("access-new", "refresh-new")
        val current = AtomicReference<OAuthCredentials?>(original)
        val generation = AtomicLong(0L)
        val enteredNetwork = CountDownLatch(1)
        val releaseNetwork = CountDownLatch(1)
        val clearCalls = AtomicInteger(0)
        val executor = Executors.newSingleThreadExecutor()

        val result = executor.submit(Callable {
            coordinator.refresh(
                observed = original,
                observedGeneration = 0L,
                currentGeneration = generation::get,
                loadCurrent = current::get,
                performRefresh = {
                    enteredNetwork.countDown()
                    assertTrue(releaseNetwork.await(5, TimeUnit.SECONDS))
                    throw OAuthException(OAuthFailureKind.REFRESH_REUSED, "stale refresh")
                },
                saveRefreshed = { current.set(it) },
                onFailure = { expected, _ ->
                    if (current.get() == expected) {
                        clearCalls.incrementAndGet()
                        current.set(null)
                    }
                },
            )
        })
        assertTrue(enteredNetwork.await(5, TimeUnit.SECONDS))
        current.set(newer)
        releaseNetwork.countDown()

        assertEquals(newer, result.get(5, TimeUnit.SECONDS))
        assertEquals(0, clearCalls.get())
        assertEquals(newer, current.get())
        executor.shutdownNow()
    }

    @Test
    fun logoutGenerationPreventsLateRefreshWriteBack() {
        val coordinator = CredentialRefreshCoordinator(Any())
        val original = credentials("access-a", "refresh-a")
        val refreshed = credentials("access-b", "refresh-b")
        val current = AtomicReference<OAuthCredentials?>(original)
        val generation = AtomicLong(0L)
        val enteredNetwork = CountDownLatch(1)
        val releaseNetwork = CountDownLatch(1)
        val saveCalls = AtomicInteger(0)
        val executor = Executors.newSingleThreadExecutor()

        val result = executor.submit(Callable {
            runCatching {
                coordinator.refresh(
                    observed = original,
                    observedGeneration = 0L,
                    currentGeneration = generation::get,
                    loadCurrent = current::get,
                    performRefresh = {
                        enteredNetwork.countDown()
                        assertTrue(releaseNetwork.await(5, TimeUnit.SECONDS))
                        refreshed
                    },
                    saveRefreshed = {
                        saveCalls.incrementAndGet()
                        current.set(it)
                    },
                    onFailure = { _, _ -> },
                )
            }.exceptionOrNull()
        })
        assertTrue(enteredNetwork.await(5, TimeUnit.SECONDS))
        generation.incrementAndGet()
        current.set(null)
        releaseNetwork.countDown()

        assertTrue(result.get(5, TimeUnit.SECONDS) is CredentialRefreshAbortedException)
        assertEquals(0, saveCalls.get())
        assertNull(current.get())
        executor.shutdownNow()
    }

    private fun credentials(accessToken: String, refreshToken: String) = OAuthCredentials(
        accessToken = accessToken,
        refreshToken = refreshToken,
        lastRefreshMillis = 1L,
    )
}
