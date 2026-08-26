package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.quota.AndroidLanAvailability
import com.codexquotatray.android.quota.LanAvailability
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.ResetCreditSnapshot
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

    fun sync(pairing: TokenSyncPairing, attempt: LanAttemptContext?): WindowsQuotaFallbackResult = sync(pairing)
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
    private val pairingStore: TokenSyncPairingStore? = null,
) : WindowsQuotaFallback {
    constructor(context: Context, client: OkHttpClient = defaultClient()) : this(
        client = client,
        diagnostics = AndroidLanDiagnosticLogger(context),
        discovery = AndroidNsdDiscovery(context),
        lanAvailability = AndroidLanAvailability(context),
        pairingStore = TokenSyncStore(context),
    )

    override fun sync(pairing: TokenSyncPairing): WindowsQuotaFallbackResult {
        return sync(pairing, null)
    }

    override fun sync(pairing: TokenSyncPairing, attempt: LanAttemptContext?): WindowsQuotaFallbackResult {
        val correlation = attempt ?: LanAttemptContext("quota", LanAttemptIds.nextId(), diagnostics)
        correlation.start(pairing)
        correlation.connectTimeout(QuotaNetworkTimeouts.WINDOWS_CONNECT_TIMEOUT_MILLIS)
        return try {
            val direct = runCatching { fetchDirect(pairing, correlation) }
            direct.getOrNull()?.let { quota ->
                correlation.finishSuccess()
                val updatedPairing = TokenSyncEndpoint.markLanSuccess(pairing, correlation)
                runCatching { pairingStore?.recordLanSuccess(pairing, correlation) }
                return WindowsQuotaFallbackResult(quota, updatedPairing)
            }
            val error = direct.exceptionOrNull()
            if (error !is WindowsQuotaFallbackException
                || error.kind != WindowsQuotaFallbackFailureKind.OFFLINE
                || !TokenSyncEndpoint.isDiscoveryEnabled(pairing)
                || discovery == null
            ) {
                correlation.finishFailure()
                throw error ?: WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "Windows quota unavailable")
            }

            correlation.nsdStart(QuotaNetworkTimeouts.WINDOWS_DNS_SD_TIMEOUT_MILLIS)
            val candidate = discovery.find(
                pairing.deviceId,
                timeoutMs = QuotaNetworkTimeouts.WINDOWS_DNS_SD_TIMEOUT_MILLIS,
                attempt = correlation,
            )
                ?.takeIf { it.deviceId.equals(pairing.deviceId, ignoreCase = true) }
                ?: run {
                    correlation.nsdTimeout()
                    throw error
                }
            val relocated = TokenSyncEndpoint.updateHost(pairing, candidate)
            correlation.nsdDiscovered(relocated.host, relocated.port)
            val quota = fetchDirect(relocated, correlation)
            correlation.finishSuccess()
            val updatedPairing = TokenSyncEndpoint.markLanSuccess(relocated, correlation)
            runCatching { pairingStore?.recordLanSuccess(pairing, correlation) }
            WindowsQuotaFallbackResult(quota, updatedPairing)
        } catch (error: Throwable) {
            correlation.finishFailure()
            runCatching { pairingStore?.recordLanFailure(pairing, correlation) }
            throw error
        }
    }

    private fun fetchDirect(pairing: TokenSyncPairing, attempt: LanAttemptContext): DirectQuotaResult {
        val safe = runCatching { TokenSyncEndpoint.validated(pairing.deviceId, pairing.host, pairing.port, pairing.secret) }
            .getOrElse {
                attempt.invalidResponse(it)
                throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.INVALID_RESPONSE, "Windows quota address invalid")
            }
        val url = "http://${safe.host}:${safe.port}/v1/quota".toHttpUrl()
        val request = Request.Builder()
            .url(url)
            .get()
            .header("Authorization", "Bearer ${safe.secret}")
            .header("Accept", "application/json")
            .build()
        val callDiagnostics = LanHttpCallDiagnostics(
            "Quota",
            diagnostics,
            attempt = attempt,
            connectTimeoutMillis = QuotaNetworkTimeouts.WINDOWS_CONNECT_TIMEOUT_MILLIS,
        )
        val response = try {
            callDiagnostics.instrument(
                client.bindToWifiLan(lanAvailability, safe.host, diagnostics, attempt),
            ).newCall(request).execute().use { result ->
                callDiagnostics.responseReceived()
                result.code to result.body?.string().orEmpty()
            }
        } catch (error: SocketTimeoutException) {
            callDiagnostics.failure("TIMEOUT", error)
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "Windows quota unavailable")
        } catch (error: IOException) {
            callDiagnostics.failure("IO", error)
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "Windows quota unavailable")
        }
        attempt.httpStatus(response.first)
        diagnostics.record("Quota LAN direct status=${response.first} elapsedMs=${callDiagnostics.elapsedMillis()}")
        if (response.first == 401) {
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.PAIRING_INVALID, "Windows pairing invalid")
        }
        if (response.first !in 200..299) {
            throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.HTTP_ERROR, "Windows quota unavailable")
        }
        return runCatching { WindowsQuotaJson.parse(response.second) }
            .onFailure { attempt.invalidResponse(it) }
            .getOrElse { throw it }
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
            resetCredits = parseResetCredits(root),
        )
    }

    private fun parseResetCredits(root: JSONObject): ResetCreditSnapshot? {
        if (!root.has("resetCredits")) return null
        val value = root.opt("resetCredits")
        if (value === JSONObject.NULL) return null
        val summary = value as? JSONObject ?: return null
        val credits = when {
            !summary.has("credits") -> null
            summary.opt("credits") === JSONObject.NULL -> null
            else -> summary.optJSONArray("credits")?.let(::parseResetCreditArray)
        }
        return ResetCreditSnapshot(
            availableCount = summary.numberOrNull("availableCount", "available_count"),
            credits = credits,
        )
    }

    private fun parseResetCreditArray(array: JSONArray): List<ResetCredit> = buildList {
        for (index in 0 until array.length()) {
            val credit = array.optJSONObject(index) ?: continue
            add(
                ResetCredit(
                    id = credit.stringOrNull("id"),
                    resetType = credit.stringOrNull("resetType") ?: credit.stringOrNull("reset_type"),
                    status = credit.stringOrNull("status"),
                    grantedAt = credit.timestampOrNull("grantedAt", "granted_at"),
                    expiresAt = credit.timestampOrNull("expiresAt", "expires_at"),
                    title = credit.stringOrNull("title"),
                    description = credit.stringOrNull("description"),
                ),
            )
        }
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
                    bucketId = window.stringOrNull("bucketId"),
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

    private fun JSONObject.numberOrNull(vararg keys: String): Long? = keys.asSequence()
        .mapNotNull { key ->
            when (val value = opt(key)) {
                is Number -> value.toLong()
                is String -> value.trim().toLongOrNull()
                else -> null
            }
        }
        .firstOrNull()

    private fun JSONObject.timestampOrNull(vararg keys: String): Long? = keys.asSequence()
        .mapNotNull { key ->
            when (val value = opt(key)) {
                is Number -> value.toLong()
                is String -> value.trim().toLongOrNull()
                    ?: runCatching { Instant.parse(value.trim()).epochSecond }.getOrNull()
                else -> null
            }
        }
        .firstOrNull()

    private fun invalid(message: String): Nothing =
        throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.INVALID_RESPONSE, message)

    private fun unsupported(): Nothing =
        throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.UNSUPPORTED, "Windows quota schema unsupported")
}
