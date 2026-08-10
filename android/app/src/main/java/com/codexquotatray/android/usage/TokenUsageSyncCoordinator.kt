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

    fun sync(pairing: TokenSyncPairing): TokenUsageSyncResult = TokenUsageSyncSingleFlight.run {
        val synced = transport.sync(pairing)
        if (!cache.save(synced.snapshot)) {
            throw TokenUsageCommitException("无法保存 Token 使用量缓存")
        }
        val updatedPairing = TokenSyncEndpoint.markSynced(synced.pairing, synced.snapshot)
        if (!pairingStore.save(updatedPairing)) {
            throw TokenUsageCommitException("无法保存 Token 同步状态")
        }
        notifyCompleted()
        synced.copy(pairing = updatedPairing)
    }
}

internal class TokenUsageCommitException(message: String) : IOException(message)

/** Shares a concurrent request's completed result instead of opening a second LAN call. */
private object TokenUsageSyncSingleFlight {
    private val monitor = Object()
    private var inFlight = false
    private var completed: Result<TokenUsageSyncResult>? = null

    fun run(block: () -> TokenUsageSyncResult): TokenUsageSyncResult {
        synchronized(monitor) {
            if (inFlight) {
                while (inFlight) {
                    try {
                        monitor.wait()
                    } catch (error: InterruptedException) {
                        Thread.currentThread().interrupt()
                        throw TokenUsageCommitException("Token 同步等待已中断")
                    }
                }
                return completed!!.getOrThrow()
            }
            inFlight = true
            completed = null
        }

        val result = runCatching(block)
        synchronized(monitor) {
            completed = result
            inFlight = false
            monitor.notifyAll()
        }
        return result.getOrThrow()
    }
}
