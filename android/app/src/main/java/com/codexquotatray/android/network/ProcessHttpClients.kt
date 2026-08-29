package com.codexquotatray.android.network

import okhttp3.OkHttpClient

/**
 * Process-level roots for clients that may safely share a dispatcher and connection pool.
 * Callers still derive their own timeout, redirect, and socket-binding configuration.
 */
internal object ProcessHttpClients {
    private val internet = OkHttpClient()
    private val lan = OkHttpClient.Builder()
        .followRedirects(false)
        .followSslRedirects(false)
        .build()

    fun internetBuilder(): OkHttpClient.Builder = internet.newBuilder()

    fun lanBuilder(): OkHttpClient.Builder = lan.newBuilder()
}
