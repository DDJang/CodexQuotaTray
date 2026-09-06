package com.codexquotatray.android.auth

import java.io.IOException
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ExecutionException

internal class CredentialRefreshAbortedException :
    IOException("credentials changed while refresh was in progress")

/**
 * Coordinates one refresh attempt without holding the process lock during
 * network I/O. Other callers await the same future outside the lock.
 */
internal class CredentialRefreshCoordinator(
    private val monitor: Any,
) {
    private data class Flight(
        val future: CompletableFuture<OAuthCredentials>,
    )

    private sealed interface FailureOutcome {
        data class Replaced(val credentials: OAuthCredentials) : FailureOutcome
        data object Aborted : FailureOutcome
        data object Propagate : FailureOutcome
    }

    private var inFlight: Flight? = null

    fun refresh(
        observed: OAuthCredentials,
        observedGeneration: Long,
        currentGeneration: () -> Long,
        loadCurrent: () -> OAuthCredentials?,
        performRefresh: (OAuthCredentials) -> OAuthCredentials,
        saveRefreshed: (OAuthCredentials) -> Unit,
        onFailure: (OAuthCredentials, Throwable) -> Unit,
    ): OAuthCredentials {
        var flight: Flight
        var owner = false
        synchronized(monitor) {
            val current = loadCurrent()
            if (current == null || currentGeneration() != observedGeneration) {
                throw CredentialRefreshAbortedException()
            }
            if (current != observed) return current

            val active = inFlight
            if (active != null) {
                flight = active
            } else {
                flight = Flight(CompletableFuture())
                inFlight = flight
                owner = true
            }
        }

        if (!owner) return await(flight.future)

        try {
            val refreshed = performRefresh(observed)
            val result = synchronized(monitor) {
                val current = loadCurrent()
                if (current == null || currentGeneration() != observedGeneration) {
                    throw CredentialRefreshAbortedException()
                }
                if (current != observed) {
                    current
                } else {
                    saveRefreshed(refreshed)
                    refreshed
                }
            }
            flight.future.complete(result)
            return result
        } catch (error: Throwable) {
            // Storage may already expose the new value in memory despite a failed
            // durable commit. Never reinterpret that failure as refresh success.
            if (error is CredentialPersistenceException) {
                flight.future.completeExceptionally(error)
                throw error
            }
            val outcome = synchronized(monitor) {
                val current = loadCurrent()
                when {
                    current == null || currentGeneration() != observedGeneration ->
                        FailureOutcome.Aborted

                    current != observed -> FailureOutcome.Replaced(current)

                    else -> {
                        runCatching { onFailure(observed, error) }
                        FailureOutcome.Propagate
                    }
                }
            }
            when (outcome) {
                is FailureOutcome.Replaced -> {
                    flight.future.complete(outcome.credentials)
                    return outcome.credentials
                }

                FailureOutcome.Aborted -> {
                    val aborted = CredentialRefreshAbortedException()
                    flight.future.completeExceptionally(aborted)
                    throw aborted
                }

                FailureOutcome.Propagate -> {
                    flight.future.completeExceptionally(error)
                    throw error
                }
            }
        } finally {
            synchronized(monitor) {
                if (inFlight === flight) inFlight = null
            }
        }
    }

    private fun await(future: CompletableFuture<OAuthCredentials>): OAuthCredentials = try {
        future.get()
    } catch (error: InterruptedException) {
        Thread.currentThread().interrupt()
        throw CredentialRefreshAbortedException()
    } catch (error: ExecutionException) {
        throw (error.cause ?: error)
    }
}

internal object ProcessCredentialRefreshCoordinator {
    val instance = CredentialRefreshCoordinator(CodexProcessLock.monitor)
}
