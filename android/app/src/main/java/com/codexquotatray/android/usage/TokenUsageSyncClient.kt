package com.codexquotatray.android.usage

import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.net.SocketTimeoutException
import java.util.concurrent.TimeUnit

enum class TokenUsageFailureKind { PAIRING_INVALID, OFFLINE, INVALID_RESPONSE, UNSUPPORTED }

class TokenUsageException(val kind: TokenUsageFailureKind, override val message: String) : IOException(message)

class TokenUsageSyncClient(private val client: OkHttpClient = defaultClient()) {
    fun fetch(pairing: TokenSyncPairing): TokenUsageSnapshot {
        val safe = runCatching { TokenSyncEndpoint.validated(pairing.host, pairing.port, pairing.secret) }
            .getOrElse { throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "同步地址无效") }
        val request = Request.Builder().url(safe.url).get()
            .header("Authorization", "Bearer ${safe.secret}")
            .header("Accept", "application/json")
            .build()
        val response = try {
            client.newCall(request).execute().use { result -> result.code to result.body?.string().orEmpty() }
        } catch (_: SocketTimeoutException) {
            throw TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
        } catch (_: IOException) {
            throw TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
        }
        if (response.first == 401) throw TokenUsageException(TokenUsageFailureKind.PAIRING_INVALID, "配对已失效")
        if (response.first !in 200..299) throw TokenUsageException(TokenUsageFailureKind.OFFLINE, "Windows 当前不可用")
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
