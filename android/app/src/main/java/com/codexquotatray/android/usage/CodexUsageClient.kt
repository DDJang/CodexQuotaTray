package com.codexquotatray.android.usage

import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.network.ProcessHttpClients
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaBucketPolicy
import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.ResetCreditSnapshot
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONTokener
import org.json.JSONObject
import java.io.IOException
import java.time.Instant
import java.time.LocalDate
import java.time.ZonedDateTime
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
    private val resetCreditsUrl: String = OAuthCredentials.RESET_CREDITS_URL,
    private val profileUrl: String = PROFILE_URL,
) {
    fun fetchTokenProfile(
        credentials: OAuthCredentials,
        callTimeoutMillis: Long? = null,
        now: ZonedDateTime = ZonedDateTime.now(),
    ): TokenUsageSnapshot {
        val request = authenticatedRequest(profileUrl, credentials).build()
        val response = execute(request, callTimeoutMillis)
        if (response.code == 401 || response.code == 403) {
            throw UsageException(UsageFailureKind.UNAUTHORIZED, "profile authentication required", response.code)
        }
        if (response.code !in 200..299) {
            throw UsageException(UsageFailureKind.SERVER, "profile API returned an error", response.code)
        }
        val json = runCatching { JSONObject(response.body) }.getOrElse {
            throw UsageException(UsageFailureKind.INVALID_RESPONSE, "profile response was not JSON")
        }
        return parseTokenProfile(json, now)
    }

    internal fun parseTokenProfile(json: JSONObject, now: ZonedDateTime = ZonedDateTime.now()): TokenUsageSnapshot {
        val stats = sequenceOf("stats", "usage", "tokenUsage")
            .mapNotNull { json.optJSONObject(it) }
            .firstOrNull() ?: json
        val summaryJson = stats.optJSONObject("summary") ?: stats
        val summaryPresent = stats.has("summary") ||
            summaryJson.has("lifetime_tokens") || summaryJson.has("lifetimeTokens") ||
            summaryJson.has("peak_daily_tokens") || summaryJson.has("peakDailyTokens") ||
            summaryJson.has("current_streak_days") || summaryJson.has("currentStreakDays") ||
            summaryJson.has("longest_streak_days") || summaryJson.has("longestStreakDays")
        val bucketsKey = sequenceOf("daily_usage_buckets", "dailyUsageBuckets").firstOrNull(stats::has)
        val bucketsPresent = bucketsKey != null && stats.opt(bucketsKey) is JSONArray
        val days = if (bucketsPresent) buildList {
            val buckets = stats.optJSONArray(bucketsKey)!!
            for (index in 0 until buckets.length()) {
                val bucket = buckets.optJSONObject(index) ?: continue
                val date = string(bucket, "start_date", "startDate")
                    ?.let { runCatching { LocalDate.parse(it) }.getOrNull() }
                val tokens = number(bucket, "tokens")?.takeIf { it >= 0L }
                if (date != null && tokens != null) add(TokenUsageDay(date, tokens, null, null, null, null))
            }
        }.groupBy(TokenUsageDay::date).map { (date, values) ->
            TokenUsageDay(date, saturatingSum(values.map(TokenUsageDay::totalTokens)), null, null, null, null)
        }.sortedBy(TokenUsageDay::date) else emptyList()
        val today = now.toLocalDate()
        val todayTokens = days.firstOrNull { it.date == today }?.totalTokens
        val last7 = if (bucketsPresent) saturatingSum(days.filter { it.date in today.minusDays(6)..today }.map(TokenUsageDay::totalTokens)) else null
        val last30 = if (bucketsPresent) saturatingSum(days.filter { it.date in today.minusDays(29)..today }.map(TokenUsageDay::totalTokens)) else null
        val summaryPeak = if (summaryPresent) number(summaryJson, "peak_daily_tokens", "peakDailyTokens")?.takeIf { it >= 0 } else null
        return TokenUsageSnapshot(
            schemaVersion = 1,
            generatedAtUtc = now.toInstant().toString(),
            sourceTimeZone = now.zone.id,
            summary = TokenUsageSummary(
                todayTokens = todayTokens,
                last7DaysTokens = last7,
                last30DaysTokens = last30,
                lifetimeTokens = if (summaryPresent) number(summaryJson, "lifetime_tokens", "lifetimeTokens")?.takeIf { it >= 0 } else null,
                peakDailyTokens = summaryPeak ?: if (bucketsPresent) days.maxOfOrNull(TokenUsageDay::totalTokens) ?: 0L else null,
                peakDate = days.maxWithOrNull(compareBy<TokenUsageDay> { it.totalTokens }.thenBy { it.date })?.date,
                activeDays = if (bucketsPresent) days.count { it.totalTokens > 0 } else null,
                currentStreak = if (summaryPresent) number(summaryJson, "current_streak_days", "currentStreakDays")?.takeIf { it >= 0 }?.coerceAtMost(Int.MAX_VALUE.toLong())?.toInt() else null,
                longestStreak = if (summaryPresent) number(summaryJson, "longest_streak_days", "longestStreakDays")?.takeIf { it >= 0 }?.coerceAtMost(Int.MAX_VALUE.toLong())?.toInt() else null,
            ),
            days = days,
            transport = DataTransport.OPENAI,
            scope = TokenUsageScope.ACCOUNT,
            source = "OAuth",
        )
    }

    fun fetch(
        credentials: OAuthCredentials,
        callTimeoutMillis: Long? = null,
    ): DirectQuotaResult {
        val requestBuilder = authenticatedRequest(usageUrl, credentials)

        val response = try {
            clientFor(callTimeoutMillis).newCall(requestBuilder.build()).execute().use { result ->
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
        val usage = parseUsage(json)
        val availableCount = usage.resetCredits?.availableCount
        if (availableCount == null || availableCount <= 0L) return usage

        // The detail endpoint is deliberately best-effort. The usage response
        // remains the successful quota result and its authoritative count is
        // retained even when this second read is unavailable.
        return usage.copy(
            resetCredits = usage.resetCredits.copy(
                credits = fetchResetCreditDetails(credentials, callTimeoutMillis),
            ),
        )
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
            bucketId = QuotaBucketPolicy.CANONICAL_BUCKET_ID,
        )
        addRateWindow(
            windows = windows,
            rateLimit = rateLimitObject,
            windowKey = "secondary",
            limitId = "secondary",
            limitName = null,
            bucketId = QuotaBucketPolicy.CANONICAL_BUCKET_ID,
        )

        val additional = rateLimitValue(json, "additional_rate_limits")
        for (index in 0 until additional.length()) {
            val entry = additional.optJSONObject(index) ?: continue
            val meteredFeature = string(entry, "metered_feature", "meteredFeature")
            val name = string(entry, "limit_name", "limitName") ?: meteredFeature
            val extraRateLimit = entry.optJSONObject("rate_limit") ?: continue
            val stableId = string(entry, "limit_id", "limitId", "id")
                ?: meteredFeature
                ?: name
                ?: "index:$index"
            val prefix = "additional:$stableId"
            addRateWindow(
                windows = windows,
                rateLimit = extraRateLimit,
                windowKey = "primary",
                limitId = "$prefix:primary",
                limitName = name,
                bucketId = stableId,
            )
            addRateWindow(
                windows = windows,
                rateLimit = extraRateLimit,
                windowKey = "secondary",
                limitId = "$prefix:secondary",
                limitName = name,
                bucketId = stableId,
            )
        }

        return DirectQuotaResult(
            planType = string(json, "plan_type"),
            windows = windows,
            quotaState = if (windows.isEmpty()) "zero_windows" else "available",
            updatedAtMillis = nowMillis,
            // Reset credits are optional metadata. A malformed summary must
            // never change the base usage result's success semantics.
            resetCredits = runCatching { parseResetCreditSummary(json) }.getOrNull(),
        )
    }

    private fun fetchResetCreditDetails(
        credentials: OAuthCredentials,
        callTimeoutMillis: Long?,
    ): List<ResetCredit>? = try {
        val requestBuilder = Request.Builder()
            .url(resetCreditsUrl)
            .get()
            .header("Authorization", "Bearer ${credentials.accessToken}")
            .header("Accept", "application/json")
            .header("User-Agent", "CodexQuotaAndroid/0.1.0")
            .header("originator", "CodexQuota Android")
        credentials.accountId
            ?.takeIf(String::isNotBlank)
            ?.let { requestBuilder.header("ChatGPT-Account-Id", it) }

        val response = clientFor(callTimeoutMillis).newCall(requestBuilder.build()).execute().use { result ->
            HttpPayload(result.code, result.body?.string().orEmpty())
        }
        if (response.code !in 200..299) null else parseResetCreditDetails(response.body)
    } catch (_: Exception) {
        null
    }

    private fun addRateWindow(
        windows: MutableList<QuotaWindow>,
        rateLimit: JSONObject,
        windowKey: String,
        limitId: String,
        limitName: String?,
        bucketId: String,
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
            bucketId = bucketId,
        )
    }

    private fun rateLimitValue(json: JSONObject, key: String): JSONArray =
        json.optJSONArray(key) ?: JSONArray()

    private fun parseResetCreditSummary(json: JSONObject): ResetCreditSnapshot? {
        val key = sequenceOf("rate_limit_reset_credits", "rateLimitResetCredits")
            .firstOrNull(json::has)
            ?: return null
        val value = json.opt(key)
        if (value === JSONObject.NULL) {
            return ResetCreditSnapshot(availableCount = null)
        }
        val summary = value as? JSONObject
            ?: return ResetCreditSnapshot(availableCount = null)
        return ResetCreditSnapshot(
            availableCount = number(summary, "available_count", "availableCount"),
            credits = parseCreditsValue(summary, "credits", "reset_credits"),
        )
    }

    internal fun parseResetCreditDetails(raw: String): List<ResetCredit> {
        val value = JSONTokener(raw).nextValue()
        val credits = when (value) {
            is JSONArray -> value
            is JSONObject -> value.optJSONArray("credits")
                ?: value.optJSONArray("reset_credits")
                ?: throw IllegalArgumentException("reset credits response missing credits")
            else -> throw IllegalArgumentException("reset credits response was not an object or array")
        }
        return parseCreditsArray(credits)
    }

    private fun parseCreditsValue(summary: JSONObject, vararg keys: String): List<com.codexquotatray.android.protocol.ResetCredit>? {
        val key = keys.firstOrNull(summary::has) ?: return null
        val value = summary.opt(key)
        if (value === JSONObject.NULL) return null
        return (value as? JSONArray)?.let { runCatching { parseCreditsArray(it) }.getOrNull() }
    }

    private fun parseCreditsArray(array: JSONArray): List<ResetCredit> = buildList {
        for (index in 0 until array.length()) {
            val credit = array.optJSONObject(index)
                ?: throw IllegalArgumentException("reset credit detail invalid at index $index")
            add(
                ResetCredit(
                    id = string(credit, "id"),
                    resetType = string(credit, "reset_type", "resetType"),
                    status = string(credit, "status"),
                    grantedAt = timestamp(credit, "granted_at", "grantedAt"),
                    expiresAt = timestamp(credit, "expires_at", "expiresAt"),
                    title = string(credit, "title"),
                    description = string(credit, "description"),
                ),
            )
        }
    }

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

    private fun timestamp(json: JSONObject, vararg keys: String): Long? = keys.asSequence()
        .mapNotNull { key ->
            when (val value = json.opt(key)) {
                is Number -> value.toLong()
                is String -> value.trim().toLongOrNull()
                    ?: runCatching { Instant.parse(value.trim()).epochSecond }.getOrNull()
                else -> null
            }
        }
        .firstOrNull()

    private data class HttpPayload(val code: Int, val body: String)

    private fun authenticatedRequest(url: String, credentials: OAuthCredentials): Request.Builder =
        Request.Builder()
            .url(url)
            .get()
            .header("Authorization", "Bearer ${credentials.accessToken}")
            .header("Accept", "application/json")
            .header("User-Agent", "CodexQuotaAndroid/0.1.0")
            .header("originator", "CodexQuota Android")
            .also { builder ->
                credentials.accountId?.takeIf(String::isNotBlank)
                    ?.let { builder.header("ChatGPT-Account-Id", it) }
            }

    private fun execute(request: Request, callTimeoutMillis: Long?): HttpPayload = try {
        clientFor(callTimeoutMillis).newCall(request).execute().use { result ->
            HttpPayload(result.code, result.body?.string().orEmpty())
        }
    } catch (_: IOException) {
        throw UsageException(UsageFailureKind.NETWORK, "usage network request failed")
    }

    private fun saturatingSum(values: Iterable<Long>): Long {
        var total = 0L
        values.forEach { value -> total = if (Long.MAX_VALUE - total < value) Long.MAX_VALUE else total + value }
        return total
    }

    internal fun clientFor(callTimeoutMillis: Long?): OkHttpClient = callTimeoutMillis
        ?.let { timeout ->
            require(timeout > 0L) { "quota call timeout must be positive" }
            httpClient.newBuilder().callTimeout(timeout, TimeUnit.MILLISECONDS).build()
        }
        ?: httpClient

    companion object {
        const val PROFILE_URL = "https://chatgpt.com/backend-api/wham/profiles/me"
        internal fun defaultClient(): OkHttpClient = ProcessHttpClients.internetBuilder()
            .connectTimeout(QuotaNetworkTimeouts.DIRECT_CONNECT_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
            .readTimeout(QuotaNetworkTimeouts.DIRECT_READ_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
            .callTimeout(QuotaNetworkTimeouts.DIRECT_CALL_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
            .build()
    }
}
