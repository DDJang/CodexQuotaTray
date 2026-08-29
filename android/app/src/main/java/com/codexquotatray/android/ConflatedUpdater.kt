package com.codexquotatray.android

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch

internal class ConflatedUpdater<T>(
    private val scope: CoroutineScope,
    private val applyUpdate: suspend (T) -> Unit,
) {
    @JvmInline
    value class Generation internal constructor(internal val value: Long)

    private data class ActiveGeneration<T>(
        val generation: Generation,
        val updates: Channel<T>,
        val worker: Job,
    )

    private val lock = Any()
    private var nextGeneration = 0L
    private var activeGeneration: ActiveGeneration<T>? = null

    fun beginGeneration(): Generation = synchronized(lock) {
        invalidateLocked(activeGeneration)
        val generation = Generation(++nextGeneration)
        val updates = Channel<T>(Channel.CONFLATED)
        val worker = scope.launch {
            for (value in updates) applyUpdate(value)
        }
        activeGeneration = ActiveGeneration(generation, updates, worker)
        generation
    }

    fun submit(generation: Generation, value: T) {
        synchronized(lock) {
            activeGeneration
                ?.takeIf { it.generation == generation }
                ?.updates
                ?.trySend(value)
        }
    }

    fun invalidate(generation: Generation) {
        synchronized(lock) {
            val active = activeGeneration?.takeIf { it.generation == generation } ?: return
            activeGeneration = null
            invalidateLocked(active)
        }
    }

    private fun invalidateLocked(active: ActiveGeneration<T>?) {
        active ?: return
        active.updates.close()
        active.worker.cancel()
    }
}
