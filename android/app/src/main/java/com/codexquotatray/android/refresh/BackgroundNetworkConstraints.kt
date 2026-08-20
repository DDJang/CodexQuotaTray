package com.codexquotatray.android.refresh

import android.net.NetworkCapabilities
import android.net.NetworkRequest
import androidx.work.Constraints
import androidx.work.NetworkType

/** The transports that may satisfy a background refresh request. */
internal enum class BackgroundNetworkTransport {
    WIFI,
    CELLULAR,
}

/** Capabilities that are deliberately explicit in a background refresh request. */
internal enum class BackgroundNetworkCapability {
    INTERNET,
    VALIDATED,
    NOT_SUSPENDED,
}

/**
 * Small, testable description of a WorkManager network requirement.
 *
 * WorkManager uses the NetworkRequest on API 28+ and the fallback NetworkType on older
 * platforms. The app's minSdk is currently higher than that fallback boundary, but keeping a
 * real fallback makes the request safe if the minSdk is ever lowered.
 */
internal data class BackgroundNetworkRequirement(
    val name: String,
    val transports: Set<BackgroundNetworkTransport>,
    val capabilities: Set<BackgroundNetworkCapability>,
    val fallbackNetworkType: NetworkType = NetworkType.CONNECTED,
) {
    val usesNetworkRequest: Boolean
        get() = transports.isNotEmpty() || capabilities.isNotEmpty()

    fun constraints(): Constraints {
        val builder = Constraints.Builder()
        if (usesNetworkRequest) {
            builder.setRequiredNetworkRequest(networkRequest(), fallbackNetworkType)
        } else {
            builder.setRequiredNetworkType(fallbackNetworkType)
        }
        return builder.build()
    }

    fun networkRequest(): NetworkRequest = NetworkRequest.Builder().apply {
        transports.forEach { transport ->
            addTransportType(
                when (transport) {
                    BackgroundNetworkTransport.WIFI -> NetworkCapabilities.TRANSPORT_WIFI
                    BackgroundNetworkTransport.CELLULAR -> NetworkCapabilities.TRANSPORT_CELLULAR
                },
            )
        }
        capabilities.forEach { capability ->
            addCapability(
                when (capability) {
                    BackgroundNetworkCapability.INTERNET -> NetworkCapabilities.NET_CAPABILITY_INTERNET
                    BackgroundNetworkCapability.VALIDATED -> NetworkCapabilities.NET_CAPABILITY_VALIDATED
                    BackgroundNetworkCapability.NOT_SUSPENDED -> NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED
                },
            )
        }
    }.build()
}

internal object BackgroundNetworkConstraints {
    /** Token data exists only on the paired Windows machine, so it needs Wi-Fi but not Internet. */
    fun token(): BackgroundNetworkRequirement = BackgroundNetworkRequirement(
        name = "TOKEN_WIFI_LAN",
        transports = setOf(BackgroundNetworkTransport.WIFI),
        capabilities = setOf(BackgroundNetworkCapability.NOT_SUSPENDED),
    )

    /**
     * Quota can use Direct HTTPS or a paired Windows fallback. With both sources, either
     * transport is useful and Internet must not be a hard requirement because the Wi-Fi path may
     * be LAN-only. Pairing without OAuth must stay on Wi-Fi so cellular cannot wake a LAN-only
     * Worker.
     */
    fun quota(
        hasOAuth: Boolean,
        hasWindowsPairing: Boolean,
    ): BackgroundNetworkRequirement = when {
        hasOAuth && hasWindowsPairing -> BackgroundNetworkRequirement(
            name = "QUOTA_WIFI_OR_CELLULAR",
            transports = setOf(
                BackgroundNetworkTransport.WIFI,
                BackgroundNetworkTransport.CELLULAR,
            ),
            capabilities = setOf(BackgroundNetworkCapability.NOT_SUSPENDED),
        )
        hasWindowsPairing -> BackgroundNetworkRequirement(
            name = "QUOTA_WIFI_LAN",
            transports = setOf(BackgroundNetworkTransport.WIFI),
            capabilities = setOf(BackgroundNetworkCapability.NOT_SUSPENDED),
        )
        else -> BackgroundNetworkRequirement(
            name = "QUOTA_VALIDATED_INTERNET",
            transports = emptySet(),
            capabilities = setOf(
                BackgroundNetworkCapability.INTERNET,
                BackgroundNetworkCapability.VALIDATED,
                BackgroundNetworkCapability.NOT_SUSPENDED,
            ),
        )
    }
}
