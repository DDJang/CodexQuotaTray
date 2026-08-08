package com.codexquotatray.android.usage

import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaWindow
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.math.roundToLong

enum class UsageFailureKind {
    UNAUTHORIZED,
    INVALID_RESPONSE,
    SERVER,
    NETWORK,
}

class UsageException(
    val kind: UsageFailureKind,
    override val message: String,
    val statusCode: Int? = null,
) : IOException(message)

class CodexUsageClient(
    private val httpClient: OkHttpClient = defaultClient(),
    private val usageUrl: String = OAuthCredentials.USAGE_URL,
) {
    fun fetch(credentials: OAuthCredentials): DirectQuotaResult {
        val requestBuilder = Request.Builder()
            .url(usageUrl)
            .get()
            .header("Authorization", "Bearer ${credentials.accessToken}")
            .header("Accept", "application/json")
            .header("User-Agent", "CodexQuotaAndroid/0.1.0")
            .header("originator", "CodexQuota Android")
        credentials.accountId
            ?.takeIf(String::isNotBlank)
            ?.let { requestBuilder.header("ChatGPT-Account-Id", it) }

        val response = try {
            httpClient.newCall(requestBuilder.build()).execute().use { result ->
                HttpPayload(result.code, result.body?.string().orEmpty())
            }
        } catch (_: IOException) {
            throw UsageException(UsageFailureKind.NETWORK, "usage network request failed")
        }
        if (response.code == 401 || response.code == 403) {
            throw UsageException(UsageFailureKind.UNAUTHORIZED, "usage authentication required", response.code)
        }
        if (response.code !in 200..299) {
            throw UsageException(UsageFailureKind.SERVER, "usage API returned an error", response.code)
        }
        val json = runCatching { JSONObject(response.body) }.getOrElse {
            throw UsageException(UsageFailureKind.INVALID_RESPONSE, "usage response was not JSON")
        }
        return parseUsage(json)
    }

    internal fun parseUsage(json: JSONObject, nowMillis: Long = System.currentTimeMillis()): DirectQuotaResult {
        val rateLimitObject = json.opt("rate_limit")
        if (rateLimitObject !is JSONObject) {
            return DirectQuotaResult(
                planType = string(json, "plan_type"),
                windows = emptyList(),
                quotaState = "unavailable",
                updatedAtMillis = nowMillis,
            )
        }

        val windows = mutableListOf<QuotaWindow>()
        addRateWindow(
            windows = windows,
            rateLimit = rateLimitObject,
            windowKey = "primary",
            limitId = "primary",
            limitName = null,
        )
        addRateWindow(
            windows = windows,
            rateLimit = rateLimitObject,
            windowKey = "secondary",
            limitId = "secondary",
            limitName = null,
        )

        val additional = rateLimitValue(json, "additional_rate_limits")
        for (index in 0 until additional.length()) {
            val entry = additional.optJSONObject(index) ?: continue
            val name = string(entry, "limit_name", "limitName")
                ?: string(entry, "metered_feature", "meteredFeature")
            val extraRateLimit = entry.optJSONObject("rate_limit") ?: continue
            val prefix = "additional:$index"
            addRateWindow(
                windows = windows,
                rateLimit = extraRateLimit,
                windowKey = "primary",
                limitId = "$prefix:primary",
                limitName = name,
            )
            addRateWindow(
                windows = windows,
                rateLimit = extraRateLimit,
                windowKey = "secondary",
                limitId = "$prefix:secondary",
                limitName = name,
            )
        }

        return DirectQuotaResult(
            planType = string(json, "plan_type"),
            windows = windows,
            quotaState = if (windows.isEmpty()) "zero_windows" else "available",
            updatedAtMillis = nowMillis,
        )
    }

    private fun addRateWindow(
        windows: MutableList<QuotaWindow>,
        rateLimit: JSONObject,
        windowKey: String,
        limitId: String,
        limitName: String?,
    ) {
        val window = rateLimit.optJSONObject("${windowKey}_window") ?: return
        val used = number(window, "used_percent")?.toInt()?.coerceIn(0, 100)
        val explicitRemaining = number(window, "remaining_percent")?.toInt()?.coerceIn(0, 100)
        val remaining = used?.let { 100 - it } ?: explicitRemaining
        val durationSeconds = number(window, "limit_window_seconds")
        windows += QuotaWindow(
            limitId = limitId,
            limitName = limitName,
            planType = null,
            sourceSlot = windowKey,
            usedPercent = used ?: explicitRemaining?.let { 100 - it },
            remainingPercent = remaining,
            windowDurationMins = durationSeconds
                ?.takeIf { it > 0L }
                ?.let { (it / 60.0).roundToLong().coerceAtLeast(1L) },
            resetsAt = number(window, "reset_at", "resets_at"),
        )
    }

    private fun rateLimitValue(json: JSONObject, key: String): JSONArray =
        json.optJSONArray(key) ?: JSONArray()

    private fun string(json: JSONObject, vararg keys: String): String? = keys.asSequence()
        .mapNotNull { key -> json.opt(key).takeIf { it is String } as String? }
        .map(String::trim)
        .firstOrNull(String::isNotEmpty)

    private fun number(json: JSONObject, vararg keys: String): Long? = keys.asSequence()
        .mapNotNull { key ->
            when (val value = json.opt(key)) {
                is Number -> value.toLong()
                is String -> value.trim().toLongOrNull()
                else -> null
            }
        }
        .firstOrNull()

    private data class HttpPayload(val code: Int, val body: String)

    companion object {
        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .callTimeout(45, TimeUnit.SECONDS)
            .build()
    }
}
