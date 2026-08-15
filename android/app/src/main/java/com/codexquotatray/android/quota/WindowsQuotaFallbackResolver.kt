package com.codexquotatray.android.quota

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.codexquotatray.android.usage.AndroidLanDiagnosticLogger
import com.codexquotatray.android.usage.LanDiagnosticLogger
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
}

/**
 * Deliberately selects a real Wi-Fi network rather than relying on activeNetwork.
 * Android may select cellular as active while Wi-Fi can still reach the paired
 * Windows host. Internet validation is not required for this local-only path.
 */
class AndroidLanAvailability(
    context: Context,
    private val diagnostics: LanDiagnosticLogger = AndroidLanDiagnosticLogger(context),
) : LanAvailability {
    private val connectivity = context.applicationContext
        .getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager

    override fun isAvailable(): Boolean = wifiNetwork() != null

    override fun socketFactoryOrNull(): SocketFactory? = wifiNetwork()?.socketFactory

    override fun isAvailableForHost(host: String): Boolean = wifiNetwork(host) != null

    override fun socketFactoryForHostOrNull(host: String): SocketFactory? = wifiNetwork(host)?.socketFactory

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
        if (!lanAvailability.isAvailable()) throw primary
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

    private fun fetchWindows(pairing: TokenSyncPairing): ResolvedQuota = try {
        val result = fallbackClient.sync(pairing)
        TokenUsagePairingLifecycle.withLock {
            val current = pairingStore.load()
            if (current == null || !current.matchesConfiguration(pairing)) {
                throw WindowsQuotaFallbackException(
                    WindowsQuotaFallbackFailureKind.PAIRING_CHANGED,
                    "Windows pairing changed; stale quota discarded",
                )
            }
            if (result.pairing != pairing) {
                val saved = pairingStore.saveIfCurrent(pairing, result.pairing)
                diagnostics.record("Quota LAN relocated endpoint persisted=$saved")
                if (!saved) {
                    throw WindowsQuotaFallbackException(
                        WindowsQuotaFallbackFailureKind.PAIRING_CHANGED,
                        "Windows pairing changed; stale quota discarded",
                    )
                }
            }
            ResolvedQuota(result.quota, result.pairing)
        }
    } catch (failure: WindowsQuotaFallbackException) {
        recordFailure(failure)
        throw failure
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
