package com.codexquotatray.android.usage

import okhttp3.Call
import okhttp3.Connection
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
    private val attempt: LanAttemptContext? = null,
    private val connectTimeoutMillis: Long? = null,
) {
    private val startedAt = System.nanoTime()
    private val phase = LanHttpCallPhase()

    val connected: Boolean
        get() = phase.connected

    fun responseReceived() {
        phase.responseHeadersStarted()
    }

    fun instrument(client: OkHttpClient): OkHttpClient = client.newBuilder()
        .eventListener(object : EventListener() {
            override fun connectStart(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy) {
                phase.connecting()
                attempt?.connectStart(inetSocketAddress)
                    ?: diagnostics.record("$label LAN connectStart elapsedMs=${elapsedMillis()}")
            }

            override fun connectEnd(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy, protocol: Protocol?) {
                attempt?.connectEnd()
                    ?: diagnostics.record("$label LAN connectEnd elapsedMs=${elapsedMillis()}")
            }

            override fun connectionAcquired(call: Call, connection: Connection) {
                phase.connectionAcquired()
                attempt?.tcpConnected(
                    connection.socket().localAddress?.hostAddress,
                    connection.socket().localPort,
                ) ?: diagnostics.record(
                    "$label LAN connectionAcquired local=${connection.socket().localAddress?.hostAddress ?: "unknown"}:${connection.socket().localPort} elapsedMs=${elapsedMillis()}",
                )
            }

            override fun connectFailed(call: Call, inetSocketAddress: InetSocketAddress, proxy: Proxy, protocol: Protocol?, ioe: IOException) {
                phase.connectFailed()
                attempt?.connectFailed(ioe, ioe is java.net.SocketTimeoutException, phase.connected)
                    ?: diagnostics.record(
                        "$label LAN connectFailed exceptionClass=${ioe.javaClass.simpleName} exceptionMessage=${ioe.message ?: "unknown"} elapsedMs=${elapsedMillis()}",
                    )
            }

            override fun responseHeadersStart(call: Call) {
                phase.responseHeadersStarted()
                attempt?.responseHeadersStart()
                    ?: diagnostics.record("$label LAN responseHeadersStart elapsedMs=${elapsedMillis()}")
            }
        })
        .build()

    fun failure(kind: String, error: IOException? = null) {
        if (attempt != null) {
            // OkHttp already reported a concrete connect failure through the
            // event listener. The outer catch only fills gaps such as a call
            // timeout before a connect callback is delivered.
            if (phase.failed) return
            if (kind.equals("TIMEOUT", ignoreCase = true)) {
                attempt.connectFailed(
                    error ?: java.net.SocketTimeoutException("$label timeout"),
                    timeout = true,
                    connectionAcquired = phase.connected,
                )
            } else {
                attempt.connectFailed(
                    error ?: IOException("$label IO failure"),
                    timeout = false,
                    connectionAcquired = phase.connected,
                )
            }
        } else {
            diagnostics.record("$label LAN direct failure=$kind phase=${phase.name} elapsedMs=${elapsedMillis()}")
        }
    }

    fun elapsedMillis(): Long = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - startedAt)
}

internal class LanHttpCallPhase {
    private enum class Phase { CREATED, CONNECTING, CONNECTED, RESPONSE_HEADERS, CONNECT_FAILED }

    @Volatile private var value = Phase.CREATED

    val connected: Boolean
        get() = value == Phase.CONNECTED || value == Phase.RESPONSE_HEADERS
    val failed: Boolean
        get() = value == Phase.CONNECT_FAILED
    val name: String
        get() = value.name

    fun connecting() { value = Phase.CONNECTING }
    fun connectionAcquired() { value = Phase.CONNECTED }
    fun responseHeadersStarted() { value = Phase.RESPONSE_HEADERS }
    fun connectFailed() { value = Phase.CONNECT_FAILED }
}
