package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient
import java.io.IOException

internal fun OkHttpClient.bindToWifiLan(lanAvailability: LanAvailability?, host: String): OkHttpClient =
    if (lanAvailability == null) this else {
        val socketFactory = lanAvailability.socketFactoryForHostOrNull(host)
            ?: throw IOException("No Wi-Fi route to paired Windows host")
        newBuilder().socketFactory(socketFactory).build()
    }
