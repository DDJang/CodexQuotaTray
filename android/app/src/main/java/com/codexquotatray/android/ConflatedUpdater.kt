package com.codexquotatray.android

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch

internal class ConflatedUpdater<T>(
    scope: CoroutineScope,
    applyUpdate: suspend (T) -> Unit,
) {
    private val updates = Channel<T>(Channel.CONFLATED)

    init {
        scope.launch {
            for (value in updates) {
                applyUpdate(value)
            }
        }
    }

    fun submit(value: T) {
        updates.trySend(value)
    }
}
