package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.quota.AndroidLanAvailability
import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.HttpUrl.Companion.toHttpUrl
import java.io.IOException
import java.net.SocketTimeoutException
import java.util.concurrent.TimeUnit

enum class TokenUsageFailureKind {
    PAIRING_INVALID,
    LOGIN_REQUIRED,
    OFFLINE,
    HTTP_ERROR,
    SERVER,
    INVALID_RESPONSE,
    UNSUPPORTED,
    UNAVAILABLE,
}

class TokenUsageException(val kind: TokenUsageFailureKind, override val message: String) : IOException(message)

data class TokenUsageSyncResult(val snapshot: TokenUsageSnapshot, val pairing: TokenSyncPairing)

class TokenUsageSyncClient(
    private val client: OkHttpClient = defaultClient(),
    private val discovery: TokenSyncDiscovery? = null,
    private val lanAvailability: LanAvailability? = null,
    private val diagnostics: LanDiagnosticLogger = NoOpLanDiagnosticLogger,
) : TokenUsageSyncTransport {
    constructor(context: Context, client: OkHttpClient = defaultClient()) : this(client, diagnostics = AndroidLanDiagnosticLogger(context), discovery = AndroidNsdDiscovery(context), lanAvailability = AndroidLanAvailability(context))

    fun fetch(pairing: TokenSyncPairing): TokenUsageSnapshot = sync(pairing).snapshot

    override fun sync(pairing: TokenSyncPairing): TokenUsageSyncResult = sync(pairing, forceRefresh = false)

    override fun sync(pairing: TokenSyncPairing, forceRefresh: Boolean): TokenUsageSyncResult {
        diagnostics.record("Token LAN stored endpoint=${pairing.host}:${pairing.port}")
        val direct = runCatching { fetchDirect(pairing, forceRefresh) }
        direct.getOrNull()?.let { return TokenUsageSyncResult(it, pairing) }
        val error = direct.exceptionOrNull()
        if (error !is TokenUsageException || !TokenSyncEndpoint.shouldDiscover(error.kind, pairing) || discovery == null) {
            throw error ?: TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
        }

        val candidate = discovery.find(pairing.deviceId)
            ?.takeIf { it.deviceId.equals(pairing.deviceId, ignoreCase = true) }
            ?: throw error
        val relocated = TokenSyncEndpoint.updateHost(pairing, candidate)
        diagnostics.record("Token LAN discovered endpoint=${relocated.host}:${relocated.port}")
        return TokenUsageSyncResult(fetchDirect(relocated, forceRefresh), relocated)
    }

    private fun fetchDirect(pairing: TokenSyncPairing, forceRefresh: Boolean): TokenUsageSnapshot {
        val safe = runCatching { TokenSyncEndpoint.validated(pairing.host, pairing.port, pairing.secret) }
            .getOrElse { throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "同步地址无效") }
        val url = safe.url.toHttpUrl().newBuilder().apply {
            if (forceRefresh) addQueryParameter("refresh", "force")
        }.build()
        val request = Request.Builder().url(url).get()
            .header("Authorization", "Bearer ${safe.secret}")
            .header("Accept", "application/json")
            .build()
        val callDiagnostics = LanHttpCallDiagnostics("Token", diagnostics)
        val response = try {
            callDiagnostics.instrument(client.bindToWifiLan(lanAvailability, safe.host, diagnostics)).newCall(request).execute().use { result ->
                callDiagnostics.responseReceived()
                result.code to result.body?.string().orEmpty()
            }
        } catch (_: SocketTimeoutException) {
            callDiagnostics.failure("TIMEOUT")
            val kind = classifyTokenTransportFailure(callDiagnostics.connected)
            throw TokenUsageException(kind, "Windows 当前不可用")
        } catch (_: IOException) {
            callDiagnostics.failure("IO")
            val kind = classifyTokenTransportFailure(callDiagnostics.connected)
            throw TokenUsageException(kind, "Windows 当前不可用")
        }
        diagnostics.record("Token LAN direct status=${response.first} elapsedMs=${callDiagnostics.elapsedMillis()}")
        if (response.first == 401) throw TokenUsageException(TokenUsageFailureKind.PAIRING_INVALID, "Windows 配对已失效，请重新扫码")
        if (response.first !in 200..299) throw TokenUsageException(TokenUsageFailureKind.HTTP_ERROR, "Windows 当前不可用")
        return TokenUsageJson.parse(response.second)
    }

    companion object {
        private fun defaultClient() = OkHttpClient.Builder()
            .connectTimeout(4, TimeUnit.SECONDS)
            .readTimeout(10, TimeUnit.SECONDS)
            .callTimeout(12, TimeUnit.SECONDS)
            .followRedirects(false)
            .followSslRedirects(false)
            .build()
    }
}

internal fun classifyTokenTransportFailure(connectionAcquired: Boolean): TokenUsageFailureKind =
    if (connectionAcquired) TokenUsageFailureKind.HTTP_ERROR else TokenUsageFailureKind.OFFLINE
