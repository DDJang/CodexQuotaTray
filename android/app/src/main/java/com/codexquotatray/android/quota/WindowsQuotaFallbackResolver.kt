package com.codexquotatray.android.quota

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import javax.net.SocketFactory
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.usage.TokenSyncPairingStore
import com.codexquotatray.android.usage.WindowsQuotaFallback
import com.codexquotatray.android.usage.WindowsQuotaFallbackException

interface LanAvailability {
    fun isAvailable(): Boolean
    fun socketFactoryOrNull(): SocketFactory? = null
}

/**
 * Deliberately selects a real Wi-Fi network rather than relying on activeNetwork.
 * Android may select cellular as active while Wi-Fi can still reach the paired
 * Windows host. Internet validation is not required for this local-only path.
 */
class AndroidLanAvailability(context: Context) : LanAvailability {
    private val connectivity = context.applicationContext
        .getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager

    override fun isAvailable(): Boolean = wifiNetwork() != null

    override fun socketFactoryOrNull(): SocketFactory? = wifiNetwork()?.socketFactory

    private fun wifiNetwork(): Network? = connectivity?.allNetworks?.firstOrNull { network ->
        connectivity.getNetworkCapabilities(network)
            ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
    }
}

/** Keeps the user-facing primary error when the optional Windows path cannot help. */
internal class WindowsQuotaFallbackResolver(
    private val pairingStore: TokenSyncPairingStore,
    private val lanAvailability: LanAvailability,
    private val fallbackClient: WindowsQuotaFallback,
    private val recordFailure: (WindowsQuotaFallbackException) -> Unit = {},
) {
    fun fetch(direct: () -> DirectQuotaResult): DirectQuotaResult = try {
        direct()
    } catch (primary: QuotaReadException) {
        if (primary.kind != QuotaReadFailureKind.NETWORK) throw primary

        val pairing = pairingStore.load() ?: throw primary
        if (!lanAvailability.isAvailable()) throw primary
        try {
            val result = fallbackClient.sync(pairing)
            if (result.pairing != pairing) {
                pairingStore.save(result.pairing)
            }
            result.quota
        } catch (failure: WindowsQuotaFallbackException) {
            recordFailure(failure)
            throw primary
        } catch (_: Exception) {
            // The optional LAN path must never replace the user's primary
            // Android NETWORK failure, including an unexpected local error.
            throw primary
        }
    }
}
