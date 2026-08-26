package com.codexquotatray.android.usage

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

interface TokenSyncDiscovery {
    fun find(deviceId: String, timeoutMs: Long = DEFAULT_TIMEOUT_MS): TokenSyncEndpoint.TokenSyncDiscoveryCandidate?

    /** Correlation-aware overload; existing discovery implementations remain compatible. */
    fun find(
        deviceId: String,
        timeoutMs: Long,
        attempt: LanAttemptContext,
    ): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? = find(deviceId, timeoutMs)

    companion object {
        const val DEFAULT_TIMEOUT_MS = 5_000L
    }
}

internal interface DiscoveryMulticastLock {
    fun acquire()
    fun release()
}

internal inline fun <T> withDiscoveryMulticastLock(lock: DiscoveryMulticastLock, block: () -> T): T {
    lock.acquire()
    return try {
        block()
    } finally {
        runCatching { lock.release() }
    }
}

private class AndroidDiscoveryMulticastLock(wifiManager: WifiManager) : DiscoveryMulticastLock {
    private val lock = wifiManager.createMulticastLock("CodexQuotaTray:nsd").apply { setReferenceCounted(false) }

    override fun acquire() = lock.acquire()
    override fun release() {
        if (lock.isHeld) lock.release()
    }
}

class AndroidNsdDiscovery(
    context: Context,
    private val diagnostics: LanDiagnosticLogger = AndroidLanDiagnosticLogger(context),
) : TokenSyncDiscovery {
    private val appContext = context.applicationContext
    private val nsd = appContext.getSystemService(Context.NSD_SERVICE) as? NsdManager
    private val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    private val main = Handler(Looper.getMainLooper())

    override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? =
        findInternal(deviceId, timeoutMs, null)

    override fun find(
        deviceId: String,
        timeoutMs: Long,
        attempt: LanAttemptContext,
    ): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? =
        findInternal(deviceId, timeoutMs, attempt)

    private fun findInternal(
        deviceId: String,
        timeoutMs: Long,
        attempt: LanAttemptContext?,
    ): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
        val manager = nsd ?: run {
            attempt?.nsdUnavailable()
            return null
        }
        val wifiManager = wifi ?: run {
            attempt?.nsdUnavailable()
            return null
        }
        if (!TokenSyncEndpoint.isValidDeviceId(deviceId)) {
            attempt?.nsdUnavailable()
            return null
        }
        return runCatching {
            withDiscoveryMulticastLock(AndroidDiscoveryMulticastLock(wifiManager)) {
                discover(manager, deviceId, timeoutMs.coerceIn(1_000L, 5_000L), attempt)
            }
        }.onFailure {
            attempt?.record("NSD multicast/discovery failure exceptionClass=${it.javaClass.simpleName}")
                ?: diagnostics.record("Windows NSD multicast/discovery failure=${it.javaClass.simpleName}")
        }.getOrNull()
    }

    private fun discover(
        manager: NsdManager,
        deviceId: String,
        timeoutMs: Long,
        attempt: LanAttemptContext?,
    ): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
        val completed = CountDownLatch(1)
        var candidate: TokenSyncEndpoint.TokenSyncDiscoveryCandidate? = null
        val resolveQueue = SerialResolveQueue<NsdServiceInfo> {
            "${it.serviceName.trim().lowercase()}|${it.serviceType.trimEnd('.').lowercase()}"
        }
        lateinit var listener: NsdManager.DiscoveryListener
        lateinit var resolveListener: NsdManager.ResolveListener
        fun resolveNext(serviceInfo: NsdServiceInfo?) {
            if (serviceInfo == null) return
            runCatching { manager.resolveService(serviceInfo, resolveListener) }
                .onFailure {
                    attempt?.record("NSD resolve start failure exceptionClass=${it.javaClass.simpleName}")
                        ?: diagnostics.record("Windows NSD resolve start failure=${it.javaClass.simpleName}")
                    resolveNext(resolveQueue.complete(matched = false))
                }
        }
        resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                attempt?.record("NSD resolve failure errorCode=$errorCode")
                    ?: diagnostics.record("Windows NSD resolve failure errorCode=$errorCode")
                resolveNext(resolveQueue.complete(matched = false))
            }

            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                val resolvedHost = serviceInfo.host?.hostAddress
                val attributes = serviceInfo.attributes
                val resolvedId = attributes.entries
                    .firstOrNull { it.key.equals("deviceId", ignoreCase = true) }
                    ?.value?.toString(Charsets.UTF_8)?.trim()
                val valid = validatedDiscoveryEndpoint(deviceId, resolvedId, resolvedHost, serviceInfo.port)
                if (valid == null) {
                    resolveNext(resolveQueue.complete(matched = false))
                    return
                }
                val name = attributes.entries
                    .firstOrNull { it.key.equals("name", ignoreCase = true) }
                    ?.value?.toString(Charsets.UTF_8)?.trim()
                candidate = TokenSyncEndpoint.TokenSyncDiscoveryCandidate(valid.deviceId, valid.host, valid.port, name)
                attempt?.nsdDiscovered(valid.host, valid.port)
                    ?: diagnostics.record("Windows NSD discovered endpoint=${valid.host}:${valid.port}")
                resolveQueue.complete(matched = true)
                completed.countDown()
            }
        }
        listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                attempt?.record("NSD started") ?: diagnostics.record("Windows NSD started")
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                attempt?.record("NSD service found") ?: diagnostics.record("Windows NSD service found")
                if (!serviceInfo.serviceType.trimEnd('.').equals(TokenSyncEndpoint.ServiceType, ignoreCase = true)) return
                resolveNext(resolveQueue.offer(serviceInfo))
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                attempt?.record("NSD start failure errorCode=$errorCode")
                    ?: diagnostics.record("Windows NSD start failure errorCode=$errorCode")
                completed.countDown()
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                attempt?.record("NSD stop failure errorCode=$errorCode")
                    ?: diagnostics.record("Windows NSD stop failure errorCode=$errorCode")
            }
        }

        runOnMainAndWait {
            attempt?.nsdStart(timeoutMs)
                ?: diagnostics.record("Windows NSD start requested timeoutMs=$timeoutMs")
            manager.discoverServices(TokenSyncEndpoint.ServiceType, NsdManager.PROTOCOL_DNS_SD, listener)
        }
        if (!completed.await(timeoutMs, TimeUnit.MILLISECONDS)) {
            attempt?.nsdTimeout() ?: diagnostics.record("Windows NSD discovery timeout")
        }
        runOnMainAndWait {
            runCatching { manager.stopServiceDiscovery(listener) }
        }
        return candidate
    }

    private fun runOnMainAndWait(action: () -> Unit) {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            action()
            return
        }
        val completed = CountDownLatch(1)
        var failure: Throwable? = null
        main.post {
            try {
                action()
            } catch (error: Throwable) {
                failure = error
            } finally {
                completed.countDown()
            }
        }
        if (!completed.await(1, TimeUnit.SECONDS)) throw IllegalStateException("NSD main-thread dispatch timed out")
        failure?.let { throw it }
    }
}

internal fun validatedDiscoveryEndpoint(
    expectedDeviceId: String,
    resolvedDeviceId: String?,
    host: String?,
    port: Int,
): TokenSyncPairing? {
    if (resolvedDeviceId == null || host == null
        || !resolvedDeviceId.equals(expectedDeviceId, ignoreCase = true)
        || !TokenSyncEndpoint.isPrivateIpv4(host)
    ) return null
    return runCatching { TokenSyncEndpoint.validated(resolvedDeviceId, host, port, "discovery") }.getOrNull()
}

internal class SerialResolveQueue<T>(private val keyOf: (T) -> String) {
    private val seen = mutableSetOf<String>()
    private val pending = ArrayDeque<T>()
    private var resolving = false
    private var completed = false

    @Synchronized
    fun offer(candidate: T): T? {
        if (completed || !seen.add(keyOf(candidate))) return null
        pending.addLast(candidate)
        return takeNext()
    }

    @Synchronized
    fun complete(matched: Boolean): T? {
        resolving = false
        if (matched) {
            completed = true
            pending.clear()
            return null
        }
        return takeNext()
    }

    private fun takeNext(): T? {
        if (completed || resolving || pending.isEmpty()) return null
        resolving = true
        return pending.removeFirst()
    }
}
