package com.codexquotatray.android.refresh

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.PowerManager
import com.codexquotatray.android.AppLogStore

/** Compact, privacy-safe network state captured at Worker entry. */
internal object AndroidWorkerNetworkDiagnostics {
    fun record(context: Context, worker: String, requirement: BackgroundNetworkRequirement) {
        val appContext = context.applicationContext
        val power = appContext.getSystemService(Context.POWER_SERVICE) as? PowerManager
        val connectivity = appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        val interactive = runCatching { power?.isInteractive }.getOrNull()
        val idle = runCatching { power?.isDeviceIdleMode }.getOrNull()
        val networks = describeNetworks(connectivity)
        val request = if (requirement.usesNetworkRequest) {
            "${requirement.name}/transports=${requirement.transports.joinToString(",")}/caps=${requirement.capabilities.joinToString(",")}"
        } else {
            requirement.name
        }
        AppLogStore.record(
            appContext,
            "$worker worker network requiredType=${if (requirement.usesNetworkRequest) "NETWORK_REQUEST" else requirement.fallbackNetworkType} " +
                "request=$request fallbackNetworkType=${requirement.fallbackNetworkType} " +
                "interactive=${interactive ?: "unknown"} idle=${idle ?: "unknown"} networks=[$networks]",
        )
    }

    @Suppress("DEPRECATION")
    private fun describeNetworks(connectivity: ConnectivityManager?): String {
        if (connectivity == null) return "unavailable"
        return runCatching {
            val active = connectivity.activeNetwork
            connectivity.allNetworks.joinToString(separator = ";") { network ->
                describeNetwork(connectivity, network, network == active)
            }.ifEmpty { "none" }
        }.getOrElse { error -> "unavailable=${error.javaClass.simpleName}" }
    }

    private fun describeNetwork(
        connectivity: ConnectivityManager,
        network: Network,
        active: Boolean,
    ): String {
        val capabilities = runCatching { connectivity.getNetworkCapabilities(network) }.getOrNull()
            ?: return "id=${network.networkHandle} active=$active caps=unavailable"
        val wifi = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
        val cellular = capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
        val transports = buildList {
            if (wifi) add("WIFI")
            if (cellular) add("CELLULAR")
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) add("ETHERNET")
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) add("VPN")
        }.ifEmpty { listOf("OTHER") }
        val restricted = !capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
        return "id=${network.networkHandle} active=$active transports=${transports.joinToString(",")} " +
            "wifi=$wifi internet=${capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)} " +
            "validated=${capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)} " +
            "notSuspended=${capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED)} " +
            "restricted=$restricted blocked=unknown"
    }
}
