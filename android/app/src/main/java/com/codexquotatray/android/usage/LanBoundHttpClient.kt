package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient
import java.io.IOException

internal fun OkHttpClient.bindToWifiLan(
    lanAvailability: LanAvailability?,
    host: String,
    diagnostics: LanDiagnosticLogger? = null,
): OkHttpClient =
    if (lanAvailability == null) this else {
        val binding = lanAvailability.socketBindingForHostOrNull(host)
            ?: throw IOException("No Wi-Fi route to paired Windows host")
        diagnostics?.record("LAN bound network=${binding.networkId ?: "unknown"}")
        newBuilder().socketFactory(binding.socketFactory).build()
    }
