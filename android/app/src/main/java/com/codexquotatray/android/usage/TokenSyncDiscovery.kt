package com.codexquotatray.android.usage

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

interface TokenSyncDiscovery {
    fun find(deviceId: String, timeoutMs: Long = DEFAULT_TIMEOUT_MS): TokenSyncEndpoint.TokenSyncDiscoveryCandidate?

    companion object {
        const val DEFAULT_TIMEOUT_MS = 4_000L
    }
}

class AndroidNsdDiscovery(context: Context) : TokenSyncDiscovery {
    private val nsd = context.applicationContext.getSystemService(Context.NSD_SERVICE) as? NsdManager
    private val main = Handler(Looper.getMainLooper())

    override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
        val manager = nsd ?: return null
        if (!TokenSyncEndpoint.isValidDeviceId(deviceId)) return null

        val completed = CountDownLatch(1)
        var candidate: TokenSyncEndpoint.TokenSyncDiscoveryCandidate? = null
        var started = false
        var timedOut = false
        var resolving = false
        lateinit var listener: NsdManager.DiscoveryListener
        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                resolving = false
            }

            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                resolving = false
                val resolvedHost = serviceInfo.host?.hostAddress ?: return
                val attributes = serviceInfo.attributes
                val resolvedId = attributes.entries
                    .firstOrNull { it.key.equals("deviceId", ignoreCase = true) }
                    ?.value
                    ?.toString(Charsets.UTF_8)
                    ?.trim()
                    ?: return
                if (!resolvedId.equals(deviceId, ignoreCase = true)) return
                if (!TokenSyncEndpoint.isPrivateIpv4(resolvedHost)) return
                val valid = runCatching {
                    TokenSyncEndpoint.validated(resolvedId, resolvedHost, serviceInfo.port, "discovery")
                }.getOrNull() ?: return
                val name = attributes.entries
                    .firstOrNull { it.key.equals("name", ignoreCase = true) }
                    ?.value
                    ?.toString(Charsets.UTF_8)
                    ?.trim()
                candidate = TokenSyncEndpoint.TokenSyncDiscoveryCandidate(valid.deviceId, valid.host, valid.port, name)
                completed.countDown()
            }
        }
        listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                started = true
                if (timedOut) runCatching { manager.stopServiceDiscovery(listener) }
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (!serviceInfo.serviceType.trimEnd('.').equals(TokenSyncEndpoint.ServiceType, ignoreCase = true) || resolving) return
                resolving = true
                runCatching { manager.resolveService(serviceInfo, resolveListener) }
                    .onFailure { resolving = false }
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit

            override fun onDiscoveryStopped(serviceType: String) = Unit

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                started = false
                completed.countDown()
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }

        main.post {
            runCatching { manager.discoverServices(TokenSyncEndpoint.ServiceType, NsdManager.PROTOCOL_DNS_SD, listener) }
                .onFailure { completed.countDown() }
        }
        completed.await(timeoutMs.coerceIn(1_000L, 5_000L), TimeUnit.MILLISECONDS)
        timedOut = true
        main.post {
            if (started) runCatching { manager.stopServiceDiscovery(listener) }
        }
        return candidate
    }
}
