package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.AppLogSanitizer
import java.io.IOException
import java.net.InetSocketAddress
import java.util.concurrent.atomic.AtomicLong

fun interface LanDiagnosticLogger {
    fun record(message: String)
}

/** Best-effort metadata already exposed by Android's selected network. */
data class LanNetworkDiagnostics(
    val networkHandle: String? = null,
    val interfaceName: String? = null,
    val localIpv4: String? = null,
    val prefixLength: Int? = null,
    val gateway: String? = null,
    val routePrefix: String? = null,
    val transports: List<String> = emptyList(),
    val capabilities: List<String> = emptyList(),
    val ssid: String? = null,
    val bssid: String? = null,
    val frequencyMhz: Int? = null,
) {
    fun toDiagnosticFields(): String = listOf(
        "matching=true",
        "networkHandle=${networkHandle ?: "unavailable"}",
        "interface=${interfaceName ?: "unavailable"}",
        "local=${localIpv4 ?: "unavailable"}",
        "prefixLength=${prefixLength ?: "unavailable"}",
        "gateway=${gateway ?: "unavailable"}",
        "routePrefix=${routePrefix ?: "unavailable"}",
        "transports=${transports.takeIf { it.isNotEmpty() }?.joinToString(",") ?: "unavailable"}",
        "capabilities=${capabilities.takeIf { it.isNotEmpty() }?.joinToString(",") ?: "unavailable"}",
        "SSID=${ssid ?: "unavailable"}",
        "BSSID=${bssid ?: "unavailable"}",
        "frequency=${frequencyMhz ?: "unavailable"}",
    ).joinToString(" ")
}

internal object NoOpLanDiagnosticLogger : LanDiagnosticLogger {
    override fun record(message: String) = Unit
}

internal class AndroidLanDiagnosticLogger(context: Context) : LanDiagnosticLogger {
    private val appContext = context.applicationContext

    override fun record(message: String) {
        // Diagnostics are deliberately fail-open: an unavailable log store
        // must never change the LAN request result.
        runCatching { AppLogStore.recordLan(appContext, message) }
    }
}

/** Process-local, concurrency-safe IDs for high-level LAN operations. */
internal object LanAttemptIds {
    private val next = AtomicLong(0L)

    fun nextId(): Long = next.updateAndGet { current ->
        if (current == Long.MAX_VALUE) 1L else current + 1L
    }
}

/**
 * Correlates the stored endpoint, TCP call, optional NSD fallback and final
 * result without changing the transport path. The pairing secret is retained
 * only in this short-lived object so exception text can be redacted before a
 * custom test logger or the persistent logger sees it.
 */
class LanAttemptContext(
    val channel: String,
    val id: Long,
    private val diagnostics: LanDiagnosticLogger,
    private val nowMillis: () -> Long = { System.currentTimeMillis() },
) {
    private val sensitiveValues = mutableListOf<String>()
    private var started = false
    private var completed = false

    @Volatile
    var finalPhase: String = "UNKNOWN"
        private set

    @Volatile
    var lastTargetEndpoint: String? = null
        private set

    val startedAtMillis: Long = nowMillis()

    @Synchronized
    fun start(pairing: TokenSyncPairing) {
        if (started) return
        started = true
        if (pairing.pairingSecret.isNotBlank()) {
            synchronized(sensitiveValues) { sensitiveValues += pairing.pairingSecret }
        }
        record("start")
        storedEndpoint(pairing.host, pairing.port)
    }

    fun storedEndpoint(host: String, port: Int) = target(host, port, "stored endpoint")

    fun target(host: String, port: Int, label: String = "target") {
        lastTargetEndpoint = "$host:$port"
        record("$label=$host:$port")
    }

    fun route(metadata: LanNetworkDiagnostics?) {
        if (metadata == null) {
            record("route matching=unavailable networkHandle=unavailable interface=unavailable local=unavailable prefixLength=unavailable gateway=unavailable routePrefix=unavailable transports=unavailable capabilities=unavailable SSID=unavailable BSSID=unavailable frequency=unavailable")
            return
        }
        record("route ${metadata.toDiagnosticFields()}")
    }

    fun routeNotFound(host: String) {
        finalPhase = "ROUTE_NOT_FOUND"
        record("route matching=false phase=ROUTE_NOT_FOUND")
    }

    fun connectTimeout(timeoutMillis: Long) = record("connectTimeoutMs=$timeoutMillis")

    fun connectStart(address: InetSocketAddress) {
        target(address.hostString, address.port, "target")
        record("connectStart")
    }

    fun connectEnd() = record("connectEnd")

    fun tcpConnected(localAddress: String?, localPort: Int?) {
        finalPhase = "TCP_CONNECTED"
        record(
            "phase=TCP_CONNECTED socketLocal=${localAddress ?: "unavailable"}:${localPort ?: "unavailable"}",
        )
    }

    fun connectFailed(error: IOException, timeout: Boolean, connectionAcquired: Boolean) {
        if (finalPhase == "ROUTE_NOT_FOUND") {
            record(
                "connectFailed phase=ROUTE_NOT_FOUND exceptionClass=${error.javaClass.simpleName} " +
                    "exceptionMessage=${redact(error.message ?: "unavailable")}",
            )
            return
        }
        finalPhase = when {
            connectionAcquired -> "HTTP_FAILED"
            timeout -> "TCP_CONNECT_TIMEOUT"
            else -> "TCP_CONNECT_IO"
        }
        record(
            "connectFailed phase=$finalPhase exceptionClass=${error.javaClass.simpleName} " +
                "exceptionMessage=${redact(error.message ?: "unavailable")}",
        )
    }

    fun responseHeadersStart() = record("responseHeadersStart")

    fun httpStatus(status: Int) {
        finalPhase = when (status) {
            401, 403 -> "AUTH_FAILED"
            in 200..299 -> "TCP_CONNECTED"
            else -> "HTTP_FAILED"
        }
        record("httpStatus=$status phase=$finalPhase")
    }

    fun invalidResponse(error: Throwable?) {
        finalPhase = "HTTP_FAILED"
        record(
            "responseParseFailed phase=HTTP_FAILED exceptionClass=${error?.javaClass?.simpleName ?: "unknown"} " +
                "exceptionMessage=${redact(error?.message ?: "unavailable")}",
        )
    }

    fun nsdStart(timeoutMillis: Long) = record("NSD start timeoutMs=$timeoutMillis")

    fun nsdTimeout() {
        finalPhase = "NSD_TIMEOUT"
        record("NSD timeout phase=NSD_TIMEOUT")
    }

    fun nsdUnavailable() = record("NSD result=unavailable")

    fun nsdDiscovered(host: String, port: Int) {
        finalPhase = "NSD_DISCOVERED"
        target(host, port, "discovered endpoint")
        record("NSD result=NSD_DISCOVERED")
    }

    fun finishSuccess() = finish("SUCCESS")

    fun finishFailure(fallbackPhase: String? = null) {
        finish(fallbackPhase ?: finalPhase.takeIf { it != "UNKNOWN" } ?: "HTTP_FAILED")
    }

    @Synchronized
    fun finish(phase: String) {
        if (completed) return
        completed = true
        finalPhase = phase
        record("result=$phase")
    }

    @Synchronized
    fun isCompleted(): Boolean = completed

    fun record(message: String) {
        runCatching {
            diagnostics.record(
                "LAN attempt=$id channel=$channel ${redact(message)}",
            )
        }
    }

    private fun redact(value: String): String {
        var safe = AppLogSanitizer.sanitizeLan(value)
        synchronized(sensitiveValues) {
            sensitiveValues.filter(String::isNotBlank).forEach { secret ->
                safe = safe.replace(secret, "[已隐藏]")
            }
        }
        return safe
    }
}
