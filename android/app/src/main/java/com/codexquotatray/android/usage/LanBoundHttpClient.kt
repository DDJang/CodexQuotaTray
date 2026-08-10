package com.codexquotatray.android.usage

import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient

internal fun OkHttpClient.bindToWifiLan(lanAvailability: LanAvailability?): OkHttpClient =
    lanAvailability?.socketFactoryOrNull()
        ?.let { socketFactory -> newBuilder().socketFactory(socketFactory).build() }
        ?: this
