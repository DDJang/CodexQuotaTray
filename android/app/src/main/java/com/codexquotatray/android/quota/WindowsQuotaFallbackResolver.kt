package com.codexquotatray.android.quota

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import com.codexquotatray.android.usage.AndroidLanDiagnosticLogger
import com.codexquotatray.android.usage.LanAttemptContext
import com.codexquotatray.android.usage.LanAttemptIds
import com.codexquotatray.android.usage.LanDiagnosticLogger
import com.codexquotatray.android.usage.LanNetworkDiagnostics
import com.codexquotatray.android.usage.NoOpLanDiagnosticLogger
import javax.net.SocketFactory
import java.net.Inet4Address
import java.net.InetAddress
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenSyncPairingStore
import com.codexquotatray.android.usage.TokenUsagePairingLifecycle
import com.codexquotatray.android.usage.WindowsQuotaFallback
import com.codexquotatray.android.usage.WindowsQuotaFallbackException
import com.codexquotatray.android.usage.WindowsQuotaFallbackFailureKind
import com.codexquotatray.android.usage.matchesConfiguration

interface LanAvailability {
    fun isAvailable(): Boolean
    fun socketFactoryOrNull(): SocketFactory? = null
    fun isAvailableForHost(host: String): Boolean = isAvailable()
    fun socketFactoryForHostOrNull(host: String): SocketFactory? = socketFactoryOrNull()
    fun socketBindingForHostOrNull(host: String): LanSocketBinding? =
        socketFactoryForHostOrNull(host)?.let { LanSocketBinding(it, null) }
}

data class LanSocketBinding(
    val socketFactory: SocketFactory,
    val networkId: String?,
    val diagnostics: LanNetworkDiagnostics? = null,
)

/**
 * Deliberately selects a real Wi-Fi network rather than relying on activeNetwork.
 * Android may select cellular as active while Wi-Fi can still reach the paired
 * Windows host. Internet validation is not required for this local-only path.
 */
class AndroidLanAvailability(
    context: Context,
    private val diagnostics: LanDiagnosticLogger = AndroidLanDiagnosticLogger(context),
) : LanAvailability {
    private val appContext = context.applicationContext
    private val connectivity = appContext
        .getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
    private val wifiManager = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager

    override fun isAvailable(): Boolean = wifiNetwork() != null

    override fun socketFactoryOrNull(): SocketFactory? = wifiNetwork()?.socketFactory

    override fun isAvailableForHost(host: String): Boolean = wifiNetwork(host) != null

    override fun socketFactoryForHostOrNull(host: String): SocketFactory? = wifiNetwork(host)?.socketFactory

    override fun socketBindingForHostOrNull(host: String): LanSocketBinding? =
        wifiNetwork(host)?.let { network ->
            LanSocketBinding(
                network.socketFactory,
                network.networkHandle.toString(),
                describeNetwork(network, host),
            )
        }

    private fun wifiNetwork(host: String? = null): Network? {
        val manager = connectivity ?: return null
        val candidates = manager.allNetworks.mapNotNull { network ->
            if (manager.getNetworkCapabilities(network)
                    ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) != true
            ) return@mapNotNull null
            val routes = manager.getLinkProperties(network)?.routes.orEmpty().mapNotNull { route ->
                val destination = route.destination
                val address = destination.address as? Inet4Address ?: return@mapNotNull null
                Ipv4Route(address.address, destination.prefixLength)
            }
            LanNetworkCandidate(network, network.networkHandle.toString(), routes)
        }
        if (host == null) return candidates.firstOrNull()?.value
        val selected = LanNetworkSelector.select(candidates, host)
        diagnostics.record(
            "Windows LAN route host=$host matching=${selected != null} network=${selected?.safeId ?: "none"}",
        )
        return selected?.value
    }

    private fun describeNetwork(network: Network, host: String): LanNetworkDiagnostics = runCatching {
        val manager = connectivity ?: return@runCatching LanNetworkDiagnostics(networkHandle = network.networkHandle.toString())
        val capabilities = manager.getNetworkCapabilities(network)
        val linkProperties = manager.getLinkProperties(network)
        val matchingRoute = linkProperties?.routes.orEmpty()
            .filter { route ->
                val address = route.destination.address as? Inet4Address
                address != null && Ipv4Route(address.address, route.destination.prefixLength)
                    .matches(InetAddress.getByName(host).address)
            }
            .maxByOrNull { it.destination.prefixLength }
        val local = linkProperties?.linkAddresses.orEmpty()
            .firstOrNull { it.address is Inet4Address }
        val gateway = matchingRoute?.gateway as? Inet4Address
        val wifiInfo = (capabilities?.transportInfo as? WifiInfo)
            ?: runCatching { wifiManager?.connectionInfo }.getOrNull()
        LanNetworkDiagnostics(
            networkHandle = network.networkHandle.toString(),
            interfaceName = linkProperties?.interfaceName,
            localIpv4 = (local?.address as? Inet4Address)?.hostAddress,
            prefixLength = local?.prefixLength,
            gateway = gateway?.hostAddress
                ?: linkProperties?.routes.orEmpty()
                    .firstOrNull { it.gateway is Inet4Address }
                    ?.gateway?.hostAddress,
            routePrefix = matchingRoute?.destination?.let { destination ->
                "${destination.address.hostAddress}/${destination.prefixLength}"
            },
            transports = capabilities?.let(::transports).orEmpty(),
            capabilities = capabilities?.let(::capabilities).orEmpty(),
            ssid = wifiInfo?.ssid?.takeUnless { it.isNullOrBlank() || it == WifiManager.UNKNOWN_SSID },
            bssid = wifiInfo?.bssid?.takeUnless { it.isNullOrBlank() || it == "02:00:00:00:00:00" },
            frequencyMhz = wifiInfo?.frequency?.takeIf { it > 0 },
        )
    }.getOrElse {
        LanNetworkDiagnostics(networkHandle = network.networkHandle.toString())
    }

    private fun transports(capabilities: NetworkCapabilities): List<String> = buildList {
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) add("WIFI")
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) add("CELLULAR")
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) add("ETHERNET")
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) add("VPN")
        if (isEmpty()) add("OTHER")
    }

    private fun capabilities(value: NetworkCapabilities): List<String> = buildList {
        if (value.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) add("INTERNET")
        if (value.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) add("VALIDATED")
        if (value.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED)) add("NOT_SUSPENDED")
        if (value.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)) add("NOT_RESTRICTED")
        if (value.hasCapability(NetworkCapabilities.NET_CAPABILITY_TRUSTED)) add("TRUSTED")
    }
}

internal data class Ipv4Route(val address: ByteArray, val prefixLength: Int) {
    fun matches(host: ByteArray): Boolean {
        if (address.size != 4 || host.size != 4 || prefixLength !in 0..32) return false
        val fullBytes = prefixLength / 8
        val remainingBits = prefixLength % 8
        for (index in 0 until fullBytes) if (address[index] != host[index]) return false
        if (remainingBits == 0) return true
        val mask = (0xff shl (8 - remainingBits)) and 0xff
        return (address[fullBytes].toInt() and mask) == (host[fullBytes].toInt() and mask)
    }
}

internal data class LanNetworkCandidate<T>(val value: T, val safeId: String, val routes: List<Ipv4Route>)

internal object LanNetworkSelector {
    fun <T> select(candidates: List<LanNetworkCandidate<T>>, host: String): LanNetworkCandidate<T>? {
        val hostBytes = runCatching { InetAddress.getByName(host) as? Inet4Address }
            .getOrNull()?.address ?: return null
        return candidates.mapNotNull { candidate ->
            candidate.routes.filter { it.matches(hostBytes) }.maxOfOrNull { it.prefixLength }
                ?.let { prefix -> candidate to prefix }
        }.maxByOrNull { it.second }?.first
    }
}

internal data class ResolvedQuota(
    val quota: DirectQuotaResult,
    val pairing: TokenSyncPairing? = null,
)

/** Keeps the user-facing primary error when the optional Windows path cannot help. */
internal class WindowsQuotaFallbackResolver(
    private val pairingStore: TokenSyncPairingStore,
    private val lanAvailability: LanAvailability,
    private val fallbackClient: WindowsQuotaFallback,
    private val recordFailure: (WindowsQuotaFallbackException) -> Unit = {},
    private val diagnostics: LanDiagnosticLogger = NoOpLanDiagnosticLogger,
) {
    fun fetchWindowsOnly(): DirectQuotaResult = fetchWindowsOnlyWithPairing().quota

    internal fun fetchWindowsOnlyWithPairing(): ResolvedQuota {
        val pairing = pairingStore.load()
            ?: throw QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "尚未登录 Codex，也未配对 Windows")
        if (!lanAvailability.isAvailable()) {
            recordUnavailable(pairing)
            throw QuotaReadException(QuotaReadFailureKind.NETWORK, "Windows 局域网暂不可用")
        }

        return try {
            fetchWindows(pairing)
        } catch (failure: WindowsQuotaFallbackException) {
            throw mapWindowsFailure(failure)
        } catch (error: Exception) {
            throw QuotaReadException(QuotaReadFailureKind.NETWORK, "Windows 局域网暂不可用", error)
        }
    }

    fun fetch(direct: () -> DirectQuotaResult): DirectQuotaResult = fetchWithPairing(direct).quota

    internal fun fetchWithPairing(direct: () -> DirectQuotaResult): ResolvedQuota = try {
        ResolvedQuota(direct())
    } catch (primary: QuotaReadException) {
        if (primary.kind != QuotaReadFailureKind.NETWORK) throw primary

        val pairing = pairingStore.load() ?: throw primary
        if (!lanAvailability.isAvailable()) {
            recordUnavailable(pairing)
            throw primary
        }
        try {
            fetchWindows(pairing)
        } catch (failure: WindowsQuotaFallbackException) {
            throw primary
        } catch (_: Exception) {
            // The optional LAN path must never replace the user's primary
            // Android NETWORK failure, including an unexpected local error.
            throw primary
        }
    }

    private fun fetchWindows(pairing: TokenSyncPairing): ResolvedQuota {
        val attempt = LanAttemptContext("quota", LanAttemptIds.nextId(), diagnostics)
        attempt.start(pairing)
        return try {
            val result = fallbackClient.sync(pairing, attempt)
            if (!attempt.isCompleted()) attempt.finishSuccess()
            // The concrete fallback client returns the pairing with its LAN
            // summary attached. Keep older injected implementations' pairing
            // shape unchanged so relocation persistence remains compatible.
            val resultPairing = result.pairing
            runCatching { pairingStore.recordLanSuccess(pairing, attempt) }
            TokenUsagePairingLifecycle.withLock {
                val current = pairingStore.load()
                if (current == null || !current.matchesConfiguration(pairing)) {
                    throw WindowsQuotaFallbackException(
                        WindowsQuotaFallbackFailureKind.PAIRING_CHANGED,
                        "Windows pairing changed; stale quota discarded",
                    )
                }
                if (resultPairing != pairing) {
                    val saved = pairingStore.saveIfCurrent(pairing, resultPairing)
                    diagnostics.record("Quota LAN relocated endpoint persisted=$saved")
                    if (!saved) {
                        throw WindowsQuotaFallbackException(
                            WindowsQuotaFallbackFailureKind.PAIRING_CHANGED,
                            "Windows pairing changed; stale quota discarded",
                        )
                    }
                }
                ResolvedQuota(result.quota, resultPairing)
            }
        } catch (failure: WindowsQuotaFallbackException) {
            attempt.finishFailure()
            runCatching { pairingStore.recordLanFailure(pairing, attempt) }
            recordFailure(failure)
            throw failure
        } catch (error: Throwable) {
            attempt.finishFailure()
            runCatching { pairingStore.recordLanFailure(pairing, attempt) }
            throw error
        }
    }

    private fun recordUnavailable(pairing: TokenSyncPairing) {
        val attempt = LanAttemptContext("quota", LanAttemptIds.nextId(), diagnostics)
        attempt.start(pairing)
        attempt.routeNotFound(pairing.host)
        attempt.finishFailure("ROUTE_NOT_FOUND")
        runCatching { pairingStore.recordLanFailure(pairing, attempt) }
    }

    private fun mapWindowsFailure(failure: WindowsQuotaFallbackException): QuotaReadException = when (failure.kind) {
        com.codexquotatray.android.usage.WindowsQuotaFallbackFailureKind.PAIRING_INVALID ->
            QuotaReadException(QuotaReadFailureKind.LOGIN_REQUIRED, "Windows 配对已失效，请重新扫码", failure)
        com.codexquotatray.android.usage.WindowsQuotaFallbackFailureKind.OFFLINE ->
            QuotaReadException(QuotaReadFailureKind.NETWORK, "Windows 局域网暂不可用", failure)
        com.codexquotatray.android.usage.WindowsQuotaFallbackFailureKind.HTTP_ERROR ->
            QuotaReadException(QuotaReadFailureKind.SERVER, "Windows 额度服务暂时不可用", failure)
        com.codexquotatray.android.usage.WindowsQuotaFallbackFailureKind.INVALID_RESPONSE,
        com.codexquotatray.android.usage.WindowsQuotaFallbackFailureKind.UNSUPPORTED,
        -> QuotaReadException(QuotaReadFailureKind.INVALID_RESPONSE, "Windows 额度数据无法识别", failure)
        WindowsQuotaFallbackFailureKind.PAIRING_CHANGED ->
            QuotaReadException(QuotaReadFailureKind.NETWORK, "Windows 配对已变更，请重试", failure)
    }
}
