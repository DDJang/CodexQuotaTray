package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.quota.AndroidLanAvailability
import com.codexquotatray.android.quota.LanAvailability
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.net.SocketTimeoutException
import java.util.concurrent.TimeUnit

enum class TokenUsageFailureKind { PAIRING_INVALID, OFFLINE, HTTP_ERROR, INVALID_RESPONSE, UNSUPPORTED }

class TokenUsageException(val kind: TokenUsageFailureKind, override val message: String) : IOException(message)

data class TokenUsageSyncResult(val snapshot: TokenUsageSnapshot, val pairing: TokenSyncPairing)

class TokenUsageSyncClient(
    private val client: OkHttpClient = defaultClient(),
    private val discovery: TokenSyncDiscovery? = null,
    private val lanAvailability: LanAvailability? = null,
) : TokenUsageSyncTransport {
    constructor(context: Context, client: OkHttpClient = defaultClient()) : this(
        client,
        AndroidNsdDiscovery(context),
        AndroidLanAvailability(context),
    )

    fun fetch(pairing: TokenSyncPairing): TokenUsageSnapshot = sync(pairing).snapshot

    override fun sync(pairing: TokenSyncPairing): TokenUsageSyncResult {
        val direct = runCatching { fetchDirect(pairing) }
        direct.getOrNull()?.let { return TokenUsageSyncResult(it, pairing) }
        val error = direct.exceptionOrNull()
        if (error !is TokenUsageException || !TokenSyncEndpoint.shouldDiscover(error.kind, pairing) || discovery == null) {
            throw error ?: TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
        }

        val candidate = discovery.find(pairing.deviceId)
            ?.takeIf { it.deviceId.equals(pairing.deviceId, ignoreCase = true) }
            ?: throw error
        val relocated = TokenSyncEndpoint.updateHost(pairing, candidate)
        return TokenUsageSyncResult(fetchDirect(relocated), relocated)
    }

    private fun fetchDirect(pairing: TokenSyncPairing): TokenUsageSnapshot {
        val safe = runCatching { TokenSyncEndpoint.validated(pairing.host, pairing.port, pairing.secret) }
            .getOrElse { throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "同步地址无效") }
        val request = Request.Builder().url(safe.url).get()
            .header("Authorization", "Bearer ${safe.secret}")
            .header("Accept", "application/json")
            .build()
        val response = try {
            client.bindToWifiLan(lanAvailability).newCall(request).execute().use { result -> result.code to result.body?.string().orEmpty() }
        } catch (_: SocketTimeoutException) {
            throw TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
        } catch (_: IOException) {
            throw TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
        }
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
