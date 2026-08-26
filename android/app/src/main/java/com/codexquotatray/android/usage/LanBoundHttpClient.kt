package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient
import java.io.IOException

internal fun OkHttpClient.bindToWifiLan(
    lanAvailability: LanAvailability?,
    host: String,
    diagnostics: LanDiagnosticLogger? = null,
): OkHttpClient = bindToWifiLan(lanAvailability, host, diagnostics, null)

internal fun OkHttpClient.bindToWifiLan(
    lanAvailability: LanAvailability?,
    host: String,
    diagnostics: LanDiagnosticLogger?,
    attempt: LanAttemptContext?,
): OkHttpClient =
    if (lanAvailability == null) this else {
        val binding = lanAvailability.socketBindingForHostOrNull(host)
            ?: run {
                attempt?.routeNotFound(host)
                throw IOException("No Wi-Fi route to paired Windows host")
            }
        attempt?.route(binding.diagnostics)
            ?: diagnostics?.record("LAN bound network=${binding.networkId ?: "unknown"}")
        newBuilder().socketFactory(binding.socketFactory).build()
    }
