package com.codexquotatray.android.usage

import android.content.Context
import java.io.IOException

internal fun interface TokenUsageSyncTransport {
    fun sync(pairing: TokenSyncPairing): TokenUsageSyncResult
}

/**
 * One process-local path for foreground, Settings, and WorkManager Token syncs.
 * The cache is committed before the successful-sync timestamp is persisted, so
 * a failed cache write can never make a stale cache appear freshly synchronized.
 */
internal class TokenUsageSyncCoordinator(
    private val transport: TokenUsageSyncTransport,
    private val cache: TokenUsageCacheStore,
    private val pairingStore: TokenSyncPairingStore,
    private val notifyCompleted: () -> Unit = {},
) {
    constructor(context: Context) : this(
        transport = TokenUsageSyncClient(context),
        cache = TokenUsageCache(context),
        pairingStore = TokenSyncStore(context),
        notifyCompleted = { TokenUsageRefreshEvents.notifyCompleted(context.applicationContext) },
    )

    fun sync(pairing: TokenSyncPairing): TokenUsageSyncResult = TokenUsageSyncSingleFlight.run(pairing.cacheIdentity()) {
        val synced = transport.sync(pairing)
        TokenUsagePairingLifecycle.withLock {
            val current = pairingStore.load()
            if (current == null || !current.matchesConfiguration(pairing)) {
                throw TokenUsagePairingChangedException()
            }
            if (!synced.pairing.deviceId.equals(pairing.deviceId, ignoreCase = true)) {
                throw TokenUsagePairingChangedException()
            }
            if (!cache.save(pairing, synced.snapshot)) {
                throw TokenUsageCommitException()
            }
            val updatedPairing = TokenSyncEndpoint.markSynced(synced.pairing, synced.snapshot)
            if (!pairingStore.saveIfCurrent(pairing, updatedPairing)) {
                if (pairingStore.load()?.matchesConfiguration(pairing) != true) {
                    throw TokenUsagePairingChangedException()
                }
                throw TokenUsageCommitException()
            }
            notifyCompleted()
            synced.copy(pairing = updatedPairing)
        }
    }
}

internal class TokenUsageCommitException : IOException("Token 同步数据保存失败")
internal class TokenUsagePairingChangedException : IOException("Windows 配对已变更，已丢弃旧 Token 同步结果")

internal fun tokenUsageSyncErrorMessage(error: Throwable): String = when (error) {
    is TokenUsageCommitException -> "Token 同步数据保存失败"
    is TokenUsagePairingChangedException -> error.message ?: "Windows 配对已变更，已丢弃旧 Token 同步结果"
    is TokenUsageException -> error.message
    else -> "Windows 当前不可用"
}

/** Shares a concurrent request's completed result instead of opening a second LAN call. */
private object TokenUsageSyncSingleFlight {
    private val monitor = Object()
    private val inFlights = mutableMapOf<String, Flight>()

    fun run(identity: String, block: () -> TokenUsageSyncResult): TokenUsageSyncResult {
        val flight: Flight
        synchronized(monitor) {
            val existing = inFlights[identity]
            if (existing != null) {
                flight = existing
                while (!flight.completed) {
                    try {
                        monitor.wait()
                    } catch (error: InterruptedException) {
                        Thread.currentThread().interrupt()
                        throw TokenUsageCommitException()
                    }
                }
                return flight.result!!.getOrThrow()
            }
            flight = Flight()
            inFlights[identity] = flight
        }

        val result = runCatching(block)
        synchronized(monitor) {
            flight.result = result
            flight.completed = true
            if (inFlights[identity] === flight) inFlights.remove(identity)
            monitor.notifyAll()
        }
        return result.getOrThrow()
    }

    private class Flight {
        var completed = false
        var result: Result<TokenUsageSyncResult>? = null
    }
}
