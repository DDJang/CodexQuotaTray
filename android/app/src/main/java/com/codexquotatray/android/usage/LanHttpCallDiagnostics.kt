package com.codexquotatray.android.usage

import okhttp3.Call
import okhttp3.EventListener
import okhttp3.OkHttpClient
import okhttp3.Protocol
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Proxy
import java.util.concurrent.TimeUnit

internal class LanHttpCallDiagnostics(
    private val label: String,
    private val diagnostics: LanDiagnosticLogger,
) {
    private enum class Phase { CREATED, CONNECTING, CONNECTED, RESPONSE_HEADERS, CONNECT_FAILED }

    private val startedAt = System.nanoTime()
    @Volatile private var phase = Phase.CREATED

    val connected: Boolean
        get() = phase == Phase.CONNECTED || phase == Phase.RESPONSE_HEADERS

    fun responseReceived() {
        phase = Phase.RESPONSE_HEADERS
    }

    fun instrument(client: OkHttpClient): OkHttpClient = client.newBuilder()
        .eventListener(object : EventListener() {
            override fun connectStart(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy) {
                phase = Phase.CONNECTING
                diagnostics.record("$label LAN connectStart elapsedMs=${elapsedMillis()}")
            }

            override fun connectEnd(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy, protocol: Protocol?) {
                phase = Phase.CONNECTED
                diagnostics.record("$label LAN connectEnd elapsedMs=${elapsedMillis()}")
            }

            override fun connectFailed(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy, protocol: Protocol?, ioe: IOException) {
                phase = Phase.CONNECT_FAILED
                diagnostics.record("$label LAN connectFailed elapsedMs=${elapsedMillis()}")
            }

            override fun responseHeadersStart(call: Call) {
                phase = Phase.RESPONSE_HEADERS
                diagnostics.record("$label LAN responseHeadersStart elapsedMs=${elapsedMillis()}")
            }
        })
        .build()

    fun failure(kind: String) {
        diagnostics.record("$label LAN direct failure=$kind phase=${phase.name} elapsedMs=${elapsedMillis()}")
    }

    fun elapsedMillis(): Long = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - startedAt)
}
