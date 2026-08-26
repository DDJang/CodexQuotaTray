package com.codexquotatray.android.usage

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Handler
import android.os.Looper
import java.net.Inet4Address
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArraySet

/**
 * Observes Wi-Fi lifecycle changes without performing any network I/O. The
 * callback only invalidates the process-local epoch and notifies foreground
 * consumers once a short burst of callbacks has settled.
 */
internal class AndroidLanNetworkLifecycle(
    context: Context,
    private val diagnostics: LanDiagnosticLogger = AndroidLanDiagnosticLogger(context),
    private val debounceMillis: Long = DEFAULT_DEBOUNCE_MILLIS,
    private val handler: Handler = Handler(Looper.getMainLooper()),
) : AutoCloseable {
    private val connectivity = context.applicationContext
        .getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
    private val callbacks = CopyOnWriteArraySet<(Long) -> Unit>()
    private val capabilitySignatures = ConcurrentHashMap<Long, String>()
    private val linkPropertiesSignatures = ConcurrentHashMap<Long, String>()
    private val debounce = LanNetworkRecoveryDebounce()
    private val stateLock = Any()
    private var started = false
    private var pendingGeneration: Long? = null

    private val recoveryRunnable = Runnable {
        val generation = synchronized(stateLock) { pendingGeneration }
        if (generation == null || !debounce.consume(generation)) return@Runnable
        synchronized(stateLock) { pendingGeneration = null }
        LanNetworkEpoch.markRecoveryAction("CONTEXT_REFRESHED")
        diagnostics.record("LAN context refreshed generation=$generation")
        callbacks.forEach { listener -> runCatching { listener(generation) } }
    }

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = notifyChange("NETWORK_AVAILABLE")

        override fun onLost(network: Network) {
            capabilitySignatures.remove(network.networkHandle)
            linkPropertiesSignatures.remove(network.networkHandle)
            notifyChange("NETWORK_LOST")
        }

        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            if (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return
            val signature = capabilitySignature(capabilities)
            val key = network.networkHandle
            if (capabilitySignatures.put(key, signature) != signature) {
                notifyChange("CAPABILITIES_CHANGED")
            }
        }

        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
            val signature = linkPropertiesSignature(linkProperties)
            val key = network.networkHandle
            if (linkPropertiesSignatures.put(key, signature) != signature) {
                notifyChange("LINK_PROPERTIES_CHANGED")
            }
        }
    }

    fun addStableListener(listener: (Long) -> Unit): AutoCloseable {
        callbacks += listener
        return AutoCloseable { callbacks -= listener }
    }

    fun start() {
        synchronized(stateLock) {
            if (started) return
            started = true
        }
        val manager = connectivity ?: run {
            diagnostics.record("LAN network callback unavailable reason=CONNECTIVITY_MANAGER")
            return
        }
        runCatching {
            manager.registerNetworkCallback(
                NetworkRequest.Builder()
                    .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                    .build(),
                callback,
            )
        }.onFailure {
            diagnostics.record("LAN network callback unavailable exceptionClass=${it.javaClass.simpleName}")
        }
    }

    override fun close() {
        handler.removeCallbacks(recoveryRunnable)
        debounce.cancel()
        synchronized(stateLock) {
            pendingGeneration = null
            if (!started) return
            started = false
        }
        runCatching { connectivity?.unregisterNetworkCallback(callback) }
        capabilitySignatures.clear()
        linkPropertiesSignatures.clear()
    }

    private fun notifyChange(reason: String) {
        synchronized(stateLock) {
            if (!started) return
        }
        val generation = LanNetworkEpoch.advance(reason, diagnostics)
        val first = debounce.schedule(generation)
        synchronized(stateLock) { pendingGeneration = generation }
        diagnostics.record(
            if (first) {
                "LAN recovery debounce scheduled generation=$generation delayMs=$debounceMillis"
            } else {
                "LAN recovery debounce coalesced generation=$generation"
            },
        )
        handler.removeCallbacks(recoveryRunnable)
        handler.postDelayed(recoveryRunnable, debounceMillis.coerceIn(1_000L, 2_000L))
    }

    private fun capabilitySignature(value: NetworkCapabilities): String = listOf(
        value.hasTransport(NetworkCapabilities.TRANSPORT_WIFI),
        value.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET),
        value.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED),
        value.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED),
        value.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED),
        value.hasCapability(NetworkCapabilities.NET_CAPABILITY_TRUSTED),
    ).joinToString(",")

    private fun linkPropertiesSignature(value: LinkProperties): String = buildString {
        append(value.interfaceName ?: "unavailable")
        append('|')
        value.linkAddresses
            .filter { it.address is Inet4Address }
            .sortedBy { it.address.hostAddress }
            .forEach { append(it.address.hostAddress).append('/').append(it.prefixLength).append(',') }
        append('|')
        value.routes
            .filter { it.destination.address is Inet4Address }
            .sortedBy { "${it.destination.address.hostAddress}/${it.destination.prefixLength}" }
            .forEach {
                append(it.destination.address.hostAddress).append('/').append(it.destination.prefixLength)
                append('@').append((it.gateway as? Inet4Address)?.hostAddress ?: "direct")
                append(',')
            }
    }

    private companion object {
        const val DEFAULT_DEBOUNCE_MILLIS = 1_500L
    }
}
