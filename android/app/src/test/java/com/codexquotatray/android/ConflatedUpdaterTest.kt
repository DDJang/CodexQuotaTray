package com.codexquotatray.android

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ConflatedUpdaterTest {
    @Test
    fun onlyLatestPendingUpdateIsApplied() = runBlocking {
        val workerJob = SupervisorJob()
        val workerScope = CoroutineScope(coroutineContext + workerJob)
        val firstUpdateStarted = CompletableDeferred<Unit>()
        val releaseFirstUpdate = CompletableDeferred<Unit>()
        val applied = mutableListOf<Int>()
        val updater = ConflatedUpdater<Int>(workerScope) { value ->
            applied += value
            if (value == 1) {
                firstUpdateStarted.complete(Unit)
                releaseFirstUpdate.await()
            }
        }

        val generation = updater.beginGeneration()
        updater.submit(generation, 1)
        firstUpdateStarted.await()
        updater.submit(generation, 2)
        updater.submit(generation, 3)
        releaseFirstUpdate.complete(Unit)

        withTimeout(1_000L) {
            while (applied.size < 2) yield()
        }
        workerJob.cancelAndJoin()

        assertEquals(listOf(1, 3), applied)
    }

    @Test
    fun invalidationDropsInFlightAndPendingUpdatesBeforeNextGeneration() = runBlocking {
        val workerJob = SupervisorJob()
        val workerScope = CoroutineScope(coroutineContext + workerJob)
        val oldUpdateStarted = CompletableDeferred<Unit>()
        val releaseOldUpdate = CompletableDeferred<Unit>()
        val newUpdateApplied = CompletableDeferred<Unit>()
        val applied = mutableListOf<Int>()
        val updater = ConflatedUpdater<Int>(workerScope) { value ->
            if (value == 1) {
                oldUpdateStarted.complete(Unit)
                releaseOldUpdate.await()
            }
            applied += value
            if (value == 3) newUpdateApplied.complete(Unit)
        }

        val oldGeneration = updater.beginGeneration()
        updater.submit(oldGeneration, 1)
        oldUpdateStarted.await()
        updater.submit(oldGeneration, 2)
        updater.invalidate(oldGeneration)
        releaseOldUpdate.complete(Unit)

        val newGeneration = updater.beginGeneration()
        updater.submit(oldGeneration, 4)
        updater.submit(newGeneration, 3)
        withTimeout(1_000L) { newUpdateApplied.await() }
        workerJob.cancelAndJoin()

        assertEquals(listOf(3), applied)
        assertTrue(newGeneration != oldGeneration)
    }
}
