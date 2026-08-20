package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.quota.AndroidLanAvailability
import com.codexquotatray.android.quota.LanAvailability
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.SocketTimeoutException
import java.time.Instant
import java.util.concurrent.TimeUnit

enum class WindowsQuotaFallbackFailureKind {
    PAIRING_INVALID,
    PAIRING_CHANGED,
    OFFLINE,
    HTTP_ERROR,
    INVALID_RESPONSE,
    UNSUPPORTED,
}

class WindowsQuotaFallbackException(
    val kind: WindowsQuotaFallbackFailureKind,
    override val message: String,
) : IOException(message)

data class WindowsQuotaFallbackResult(
    val quota: DirectQuotaResult,
    val pairing: TokenSyncPairing,
)

interface WindowsQuotaFallback {
    fun sync(pairing: TokenSyncPairing): WindowsQuotaFallbackResult
}

/**
 * Reads only the paired Windows runtime's last successful quota projection.
 * It shares the pairing, address validation, and one-shot discovery rules with
 * TokenUsageSyncClient, but never initiates a new pairing or follows redirects.
 */
class WindowsQuotaFallbackClient(
    private val client: OkHttpClient = defaultClient(),
    private val discovery: TokenSyncDiscovery? = null,
    private val lanAvailability: LanAvailability? = null,
    private val diagnostics: LanDiagnosticLogger = NoOpLanDiagnosticLogger,
) : WindowsQuotaFallback {
    constructor(context: Context, client: OkHttpClient = defaultClient()) : this(client, diagnostics = AndroidLanDiagnosticLogger(context), discovery = AndroidNsdDiscovery(context), lanAvailability = AndroidLanAvailability(context))

    override fun sync(pairing: TokenSyncPairing): WindowsQuotaFallbackResult {
        diagnostics.record("Quota LAN stored endpoint=${pairing.host}:${pairing.port}")
        val direct = runCatching { fetchDirect(pairing) }
        direct.getOrNull()?.let { return WindowsQuotaFallbackResult(it, pairing) }
        val error = direct.exceptionOrNull()
        if (error !is WindowsQuotaFallbackException
            || error.kind != WindowsQuotaFallbackFailureKind.OFFLINE
            || !TokenSyncEndpoint.isDiscoveryEnabled(pairing)
            || discovery == null
        ) {
            throw error ?: WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "Windows quota unavailable")
        }

        val candidate = discovery.find(
            pairing.deviceId,
            timeoutMs = QuotaNetworkTimeouts.WINDOWS_DNS_SD_TIMEOUT_MILLIS,
        )
            ?.takeIf { it.deviceId.equals(pairing.deviceId, ignoreCase = true) }
            ?: throw error
        val relocated = TokenSyncEndpoint.updateHost(pairing, candidate)
        diagnostics.record("Quota LAN discovered endpoint=${relocated.host}:${relocated.port}")
        return WindowsQuotaFallbackResult(fetchDirect(relocated), relocated)
    }

    private fun fetchDirect(pairing: TokenSyncPairing): DirectQuotaResult {
        val safe = runCatching { TokenSyncEndpoint.validated(pairing.deviceId, pairing.host, pairing.port, pairing.secret) }
            .getOrElse { throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.INVALID_RESPONSE, "Windows quota address invalid") }
        val url = "http://${safe.host}:${safe.port}/v1/quota".toHttpUrl()
        val request = Request.Builder()
            .url(url)
            .get()
            .header("Authorization", "Bearer ${safe.secret}")
            .header("Accept", "application/json")
            .build()
        val callDiagnostics = LanHttpCallDiagnostics("Quota", diagnostics)
        val response = try {
            callDiagnostics.instrument(client.bindToWifiLan(lanAvailability, safe.host, diagnostics)).newCall(request).execute().use { result ->
                callDiagnostics.responseReceived()
                result.code to result.body?.string().orEmpty()
            }
        } catch (_: SocketTimeoutException) {
            callDiagnostics.failure("TIMEOUT")
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "Windows quota unavailable")
        } catch (_: IOException) {
            callDiagnostics.failure("IO")
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "Windows quota unavailable")
        }
        diagnostics.record("Quota LAN direct status=${response.first} elapsedMs=${callDiagnostics.elapsedMillis()}")
        if (response.first == 401) {
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.PAIRING_INVALID, "Windows pairing invalid")
        }
        if (response.first !in 200..299) {
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.HTTP_ERROR, "Windows quota unavailable")
        }
        return WindowsQuotaJson.parse(response.second)
    }

    companion object {
        internal fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(QuotaNetworkTimeouts.WINDOWS_CONNECT_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
            .readTimeout(QuotaNetworkTimeouts.WINDOWS_READ_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
            .callTimeout(QuotaNetworkTimeouts.WINDOWS_CALL_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
            .followRedirects(false)
            .followSslRedirects(false)
            .build()
    }
}

internal object WindowsQuotaJson {
    fun parse(raw: String): DirectQuotaResult {
        val root = runCatching { JSONObject(raw) }.getOrElse { invalid("Windows quota was not JSON") }
        val schemaVersion = root.intOrNull("schemaVersion") ?: invalid("Windows quota schema missing")
        if (schemaVersion != 1) unsupported()
        val updatedAtMillis = root.stringOrNull("generatedAtUtc")
            ?.let { runCatching { Instant.parse(it).toEpochMilli() }.getOrNull() }
            ?: invalid("Windows quota timestamp invalid")
        val quotaState = root.stringOrNull("quotaState")
            ?.takeIf { it == "available" || it == "zero_windows" }
            ?: invalid("Windows quota state invalid")
        val windowsJson = root.optJSONArray("windows") ?: invalid("Windows quota windows missing")
        val planType = root.stringOrNull("planType")
        return DirectQuotaResult(
            planType = planType,
            windows = parseWindows(windowsJson),
            quotaState = quotaState,
            updatedAtMillis = updatedAtMillis,
            source = QuotaSource.WINDOWS,
        )
    }

    private fun parseWindows(windows: JSONArray): List<QuotaWindow> = buildList {
        for (index in 0 until windows.length()) {
            val window = windows.optJSONObject(index) ?: invalid("Windows quota window invalid")
            val used = window.percentOrNull("usedPercent")
            val remaining = window.percentOrNull("remainingPercent")
            add(
                QuotaWindow(
                    limitId = window.stringOrNull("limitId"),
                    limitName = window.stringOrNull("limitName"),
                    planType = window.stringOrNull("planType"),
                    sourceSlot = window.stringOrNull("sourceSlot") ?: "windows:$index",
                    usedPercent = used ?: remaining?.let { 100 - it },
                    remainingPercent = remaining ?: used?.let { 100 - it },
                    windowDurationMins = window.positiveLongOrNull("windowDurationMins"),
                    resetsAt = window.nonNegativeLongOrNull("resetsAt"),
                ),
            )
        }
    }

    private fun JSONObject.stringOrNull(key: String): String? = opt(key).takeIf { it is String } as String?

    private fun JSONObject.intOrNull(key: String): Int? = numberOrNull(key)?.let { value ->
        value.takeIf { it in Int.MIN_VALUE.toLong()..Int.MAX_VALUE.toLong() }?.toInt()
    }

    private fun JSONObject.percentOrNull(key: String): Int? {
        val value = intOrNull(key) ?: return null
        if (value !in 0..100) invalid("Windows quota percent invalid")
        return value
    }

    private fun JSONObject.positiveLongOrNull(key: String): Long? = numberOrNull(key)?.let { value ->
        if (value <= 0L) null else value
    }

    private fun JSONObject.nonNegativeLongOrNull(key: String): Long? = numberOrNull(key)?.let { value ->
        if (value < 0L) invalid("Windows quota reset invalid")
        value
    }

    private fun JSONObject.numberOrNull(key: String): Long? = when (val value = opt(key)) {
        is Number -> value.toLong()
        is String -> value.trim().toLongOrNull()
        else -> null
    }

    private fun invalid(message: String): Nothing =
        throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.INVALID_RESPONSE, message)

    private fun unsupported(): Nothing =
        throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.UNSUPPORTED, "Windows quota schema unsupported")
}
