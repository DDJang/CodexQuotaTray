package com.codexquotatray.android.quota

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.usage.TokenSyncPairingStore
import com.codexquotatray.android.usage.WindowsQuotaFallback
import com.codexquotatray.android.usage.WindowsQuotaFallbackException

interface LanAvailability {
    fun isAvailable(): Boolean
}

/**
 * Deliberately checks the local Wi-Fi transport only. Internet validation is not
 * required because the fallback is specifically for Wi-Fi that lost the Internet
 * while the paired Windows host remains reachable.
 */
class AndroidLanAvailability(context: Context) : LanAvailability {
    private val connectivity = context.applicationContext
        .getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager

    override fun isAvailable(): Boolean {
        val manager = connectivity ?: return false
        val network = manager.activeNetwork ?: return false
        val capabilities = manager.getNetworkCapabilities(network) ?: return false
        return capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
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
