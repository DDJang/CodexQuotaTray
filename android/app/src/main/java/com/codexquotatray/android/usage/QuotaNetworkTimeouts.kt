package com.codexquotatray.android.usage

/** Timeout policy for quota reading only; OAuth and Token Usage keep their own policies. */
internal object QuotaNetworkTimeouts {
    const val DIRECT_CONNECT_TIMEOUT_MILLIS = 5_000L
    const val DIRECT_READ_TIMEOUT_MILLIS = 8_000L
    const val DIRECT_CALL_TIMEOUT_MILLIS = 10_000L
    const val DIRECT_PAIRED_WIFI_CALL_TIMEOUT_MILLIS = 8_000L

    const val WINDOWS_CONNECT_TIMEOUT_MILLIS = 2_000L
    const val WINDOWS_READ_TIMEOUT_MILLIS = 4_000L
    const val WINDOWS_CALL_TIMEOUT_MILLIS = 5_000L
    const val WINDOWS_DNS_SD_TIMEOUT_MILLIS = 3_000L

    fun directCallTimeoutMillis(windowsPairingOnWifi: Boolean): Long =
        if (windowsPairingOnWifi) {
            DIRECT_PAIRED_WIFI_CALL_TIMEOUT_MILLIS
        } else {
            DIRECT_CALL_TIMEOUT_MILLIS
        }
}
