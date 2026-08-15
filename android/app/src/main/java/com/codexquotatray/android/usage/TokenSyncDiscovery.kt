package com.codexquotatray.android.usage

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.Looper
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

interface TokenSyncDiscovery {
    fun find(deviceId: String, timeoutMs: Long = DEFAULT_TIMEOUT_MS): TokenSyncEndpoint.TokenSyncDiscoveryCandidate?

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

    override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
        val manager = nsd ?: return null
        val wifiManager = wifi ?: return null
        if (!TokenSyncEndpoint.isValidDeviceId(deviceId)) return null
        return runCatching {
            withDiscoveryMulticastLock(AndroidDiscoveryMulticastLock(wifiManager)) {
                discover(manager, deviceId, timeoutMs.coerceIn(1_000L, 5_000L))
            }
        }.onFailure {
            diagnostics.record("Windows NSD multicast/discovery failure=${it.javaClass.simpleName}")
        }.getOrNull()
    }

    private fun discover(
        manager: NsdManager,
        deviceId: String,
        timeoutMs: Long,
    ): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
        val completed = CountDownLatch(1)
        var candidate: TokenSyncEndpoint.TokenSyncDiscoveryCandidate? = null
        val resolving = AtomicBoolean(false)
        lateinit var listener: NsdManager.DiscoveryListener
        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                resolving.set(false)
                diagnostics.record("Windows NSD resolve failure errorCode=$errorCode")
            }

            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                resolving.set(false)
                val resolvedHost = serviceInfo.host?.hostAddress ?: return
                val attributes = serviceInfo.attributes
                val resolvedId = attributes.entries
                    .firstOrNull { it.key.equals("deviceId", ignoreCase = true) }
                    ?.value?.toString(Charsets.UTF_8)?.trim() ?: return
                if (!resolvedId.equals(deviceId, ignoreCase = true)) return
                if (!TokenSyncEndpoint.isPrivateIpv4(resolvedHost)) return
                val valid = runCatching {
                    TokenSyncEndpoint.validated(resolvedId, resolvedHost, serviceInfo.port, "discovery")
                }.getOrNull() ?: return
                val name = attributes.entries
                    .firstOrNull { it.key.equals("name", ignoreCase = true) }
                    ?.value?.toString(Charsets.UTF_8)?.trim()
                candidate = TokenSyncEndpoint.TokenSyncDiscoveryCandidate(valid.deviceId, valid.host, valid.port, name)
                diagnostics.record("Windows NSD discovered endpoint=${valid.host}:${valid.port}")
                completed.countDown()
            }
        }
        listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                diagnostics.record("Windows NSD started")
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                diagnostics.record("Windows NSD service found")
                if (!serviceInfo.serviceType.trimEnd('.').equals(TokenSyncEndpoint.ServiceType, ignoreCase = true)
                    || !resolving.compareAndSet(false, true)
                ) return
                runCatching { manager.resolveService(serviceInfo, resolveListener) }
                    .onFailure {
                        resolving.set(false)
                        diagnostics.record("Windows NSD resolve start failure=${it.javaClass.simpleName}")
                    }
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                diagnostics.record("Windows NSD start failure errorCode=$errorCode")
                completed.countDown()
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                diagnostics.record("Windows NSD stop failure errorCode=$errorCode")
            }
        }

        runOnMainAndWait {
            diagnostics.record("Windows NSD start requested timeoutMs=$timeoutMs")
            manager.discoverServices(TokenSyncEndpoint.ServiceType, NsdManager.PROTOCOL_DNS_SD, listener)
        }
        if (!completed.await(timeoutMs, TimeUnit.MILLISECONDS)) {
            diagnostics.record("Windows NSD discovery timeout")
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
