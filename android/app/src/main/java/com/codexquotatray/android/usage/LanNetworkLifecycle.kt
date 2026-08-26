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
import java.util.concurrent.CopyOnWriteArraySet

/**
 * The small subset of a Wi-Fi network context that can affect a LAN attempt.
 * Callback delivery itself is deliberately not part of this value: Android
 * may deliver the same state through several callback methods.
 */
internal data class LanNetworkContextSnapshot(
    val networkHandle: Long,
    val interfaceName: String?,
    val localIpv4: String?,
    val prefixLength: String?,
    val gateway: String?,
    val routePrefix: String?,
    val transports: String,
    val capabilities: String,
    val lanEligible: Boolean,
)

internal enum class LanNetworkSnapshotUpdate {
    BASELINE,
    NO_CHANGE,
    CHANGED,
}

/** Compares LAN context values while keeping the initial callback as baseline. */
internal class LanNetworkSnapshotTracker {
    private var baselineEstablished = false
    private var current: LanNetworkContextSnapshot? = null

    internal val hasBaseline: Boolean
        get() = baselineEstablished

    internal fun observe(next: LanNetworkContextSnapshot?): LanNetworkSnapshotUpdate {
        if (!baselineEstablished) {
            baselineEstablished = true
            current = next
            return LanNetworkSnapshotUpdate.BASELINE
        }
        if (current == next) return LanNetworkSnapshotUpdate.NO_CHANGE
        current = next
        return LanNetworkSnapshotUpdate.CHANGED
    }

    internal fun reset() {
        baselineEstablished = false
        current = null
    }
}

/**
 * Observes Wi-Fi lifecycle changes without performing any network I/O. The
 * callback only invalidates the process-local epoch when the key LAN context
 * changes, then notifies foreground consumers once a short burst settles.
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
    private val networkSnapshots = mutableMapOf<Long, LanNetworkContextSnapshot>()
    private val snapshotTracker = LanNetworkSnapshotTracker()
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
        override fun onAvailable(network: Network) = observeNetwork(network, "NETWORK_AVAILABLE")

        override fun onLost(network: Network) {
            synchronized(stateLock) {
                networkSnapshots.remove(network.networkHandle)
            }
            notifySnapshotChanged("NETWORK_LOST")
        }

        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            observeNetwork(network, "CAPABILITIES_CHANGED", capabilities = capabilities)
        }

        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
            observeNetwork(network, "LINK_PROPERTIES_CHANGED", linkProperties = linkProperties)
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
            captureActiveNetwork()
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
            networkSnapshots.clear()
            snapshotTracker.reset()
        }
        runCatching { connectivity?.unregisterNetworkCallback(callback) }
    }

    private fun captureActiveNetwork() {
        val network = runCatching { connectivity?.activeNetwork }.getOrNull() ?: return
        observeNetwork(network, "NETWORK_BASELINE")
    }

    private fun observeNetwork(
        network: Network,
        reason: String,
        capabilities: NetworkCapabilities? = null,
        linkProperties: LinkProperties? = null,
    ) {
        val snapshot = snapshotFor(network, capabilities, linkProperties) ?: return
        synchronized(stateLock) {
            if (!started) return
            networkSnapshots[network.networkHandle] = snapshot
        }
        notifySnapshotChanged(reason)
    }

    private fun notifySnapshotChanged(reason: String) {
        val update = synchronized(stateLock) {
            if (!started) return
            val selected = selectedSnapshotLocked()
            if (!snapshotTracker.hasBaseline && selected == null) return
            snapshotTracker.observe(selected)
        }

        when (update) {
            LanNetworkSnapshotUpdate.BASELINE -> {
                val baseline = synchronized(stateLock) { selectedSnapshotLocked() }
                diagnostics.record(
                    "LAN network baseline established ${baseline?.toDiagnosticFields() ?: "unavailable"}",
                )
            }
            LanNetworkSnapshotUpdate.NO_CHANGE -> Unit
            LanNetworkSnapshotUpdate.CHANGED -> notifyChange(reason)
        }
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

    private fun snapshotFor(
        network: Network,
        capabilitiesOverride: NetworkCapabilities?,
        linkPropertiesOverride: LinkProperties?,
    ): LanNetworkContextSnapshot? {
        val manager = connectivity ?: return null
        val capabilities = capabilitiesOverride
            ?: runCatching { manager.getNetworkCapabilities(network) }.getOrNull()
            ?: return null
        val linkProperties = linkPropertiesOverride
            ?: runCatching { manager.getLinkProperties(network) }.getOrNull()
            ?: return null
        val addresses = linkProperties.linkAddresses
            .filter { it.address is Inet4Address }
            .sortedBy { it.address.hostAddress }
        val routes = linkProperties.routes
            .filter { it.destination.address is Inet4Address }
            .sortedBy { "${it.destination.address.hostAddress}/${it.destination.prefixLength}" }
        val interfaceName = linkProperties.interfaceName
        val localIpv4 = addresses.joinToString(",") { it.address.hostAddress.orEmpty() }
        val prefixLength = addresses.joinToString(",") { it.prefixLength.toString() }
        val routePrefix = routes.joinToString(";") {
            "${it.destination.address.hostAddress}/${it.destination.prefixLength}@" +
                ((it.gateway as? Inet4Address)?.hostAddress ?: "direct")
        }
        val defaultRoute = routes.firstOrNull { it.destination.prefixLength == 0 }
        val gateway = (defaultRoute?.gateway as? Inet4Address)?.hostAddress
        val transports = buildList {
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) add("WIFI")
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) add("CELLULAR")
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) add("ETHERNET")
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) add("VPN")
        }.ifEmpty { listOf("OTHER") }.joinToString(",")
        val capabilityNames = buildList {
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) add("WIFI")
            if (capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED)) add("NOT_SUSPENDED")
            if (capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)) add("NOT_RESTRICTED")
        }.joinToString(",")
        val lanEligible = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
            && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
            && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED)
            && !interfaceName.isNullOrBlank()
            && addresses.isNotEmpty()
            && routes.isNotEmpty()
        return LanNetworkContextSnapshot(
            networkHandle = network.networkHandle,
            interfaceName = interfaceName,
            localIpv4 = localIpv4,
            prefixLength = prefixLength,
            gateway = gateway,
            routePrefix = routePrefix,
            transports = transports,
            capabilities = capabilityNames,
            lanEligible = lanEligible,
        )
    }

    private fun selectedSnapshotLocked(): LanNetworkContextSnapshot? {
        val activeHandle = runCatching { connectivity?.activeNetwork?.networkHandle }.getOrNull()
        return networkSnapshots.values
            .filter { it.lanEligible }
            .sortedWith(
                compareBy<LanNetworkContextSnapshot> { if (it.networkHandle == activeHandle) 0 else 1 }
                    .thenBy { it.networkHandle },
            )
            .firstOrNull()
    }

    private fun LanNetworkContextSnapshot.toDiagnosticFields(): String = listOf(
        "networkHandle=$networkHandle",
        "interface=${interfaceName ?: "unavailable"}",
        "local=${localIpv4 ?: "unavailable"}",
        "prefixLength=${prefixLength ?: "unavailable"}",
        "gateway=${gateway ?: "unavailable"}",
        "routePrefix=${routePrefix ?: "unavailable"}",
        "transports=${transports.ifBlank { "unavailable" }}",
        "capabilities=${capabilities.ifBlank { "unavailable" }}",
        "lanEligible=$lanEligible",
    ).joinToString(" ")

    private companion object {
        const val DEFAULT_DEBOUNCE_MILLIS = 1_500L
    }
}
