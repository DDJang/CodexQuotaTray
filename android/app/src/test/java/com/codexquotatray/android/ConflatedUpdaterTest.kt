package com.codexquotatray.android

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
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

        updater.submit(1)
        firstUpdateStarted.await()
        updater.submit(2)
        updater.submit(3)
        releaseFirstUpdate.complete(Unit)

        withTimeout(1_000L) {
            while (applied.size < 2) yield()
        }
        workerJob.cancelAndJoin()

        assertEquals(listOf(1, 3), applied)
    }
}
