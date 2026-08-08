package com.codexquotatray.android.usage

import org.json.JSONObject
import java.net.URI
import java.time.LocalDate
import kotlin.math.ln

data class TokenUsageDay(
    val date: LocalDate,
    val totalTokens: Long,
    val inputTokens: Long?,
    val cachedInputTokens: Long?,
    val outputTokens: Long?,
    val reasoningTokens: Long?,
)

data class TokenUsageSummary(
    val todayTokens: Long,
    val last7DaysTokens: Long,
    val last30DaysTokens: Long,
    val lifetimeTokens: Long,
    val peakDailyTokens: Long,
    val peakDate: LocalDate?,
    val activeDays: Int,
    val currentStreak: Int,
    val longestStreak: Int,
)

data class TokenUsageSnapshot(
    val schemaVersion: Int,
    val generatedAtUtc: String,
    val sourceTimeZone: String,
    val summary: TokenUsageSummary,
    val days: List<TokenUsageDay>,
)

data class TokenSyncPairing(val host: String, val port: Int, val secret: String) {
    val url: String get() = "http://$host:$port/v1/token-usage"
}

object TokenSyncEndpoint {
    fun parsePairingUri(raw: String): TokenSyncPairing {
        val uri = runCatching { URI(raw.trim()) }.getOrElse { throw IllegalArgumentException("配对信息格式无效") }
        require(uri.scheme.equals("codexquota", ignoreCase = true) && uri.host.equals("pair", ignoreCase = true)) {
            "配对信息格式无效"
        }
        val query = uri.rawQuery.orEmpty().split('&').mapNotNull { part ->
            val separator = part.indexOf('=')
            if (separator <= 0) null else java.net.URLDecoder.decode(part.substring(0, separator), "UTF-8") to
                java.net.URLDecoder.decode(part.substring(separator + 1), "UTF-8")
        }.toMap()
        return validated(query["host"].orEmpty(), query["port"]?.toIntOrNull() ?: 43821, query["token"].orEmpty())
    }

    fun parseManual(address: String, secret: String): TokenSyncPairing {
        val trimmed = address.trim()
        val separator = trimmed.lastIndexOf(':')
        val host = if (separator > 0) trimmed.substring(0, separator) else trimmed
        val port = if (separator > 0) trimmed.substring(separator + 1).toIntOrNull() ?: throw IllegalArgumentException("端口无效") else 43821
        return validated(host, port, secret.trim())
    }

    fun validated(host: String, port: Int, secret: String): TokenSyncPairing {
        require(isPrivateIpv4(host)) { "仅允许私人局域网 IPv4 地址" }
        require(port in 1..65535) { "端口无效" }
        require(secret.isNotBlank()) { "配对密钥不能为空" }
        return TokenSyncPairing(host, port, secret)
    }

    fun isPrivateIpv4(host: String): Boolean {
        val parts = host.split('.')
        if (parts.size != 4) return false
        val bytes = parts.map { value -> value.toIntOrNull()?.takeIf { it in 0..255 } ?: return false }
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] in 16..31 || bytes[0] == 192 && bytes[1] == 168
    }
}

object TokenUsageJson {
    fun parse(raw: String): TokenUsageSnapshot {
        val root = runCatching { JSONObject(raw) }.getOrElse { throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "Token 数据不是有效 JSON") }
        val schema = root.strictInt("schemaVersion")
        if (schema != 1) throw TokenUsageException(TokenUsageFailureKind.UNSUPPORTED, "不支持的 Token 数据版本")
        val summaryJson = root.optJSONObject("summary") ?: invalid()
        val summary = TokenUsageSummary(
            summaryJson.strictLong("todayTokens"),
            summaryJson.strictLong("last7DaysTokens"),
            summaryJson.strictLong("last30DaysTokens"),
            summaryJson.strictLong("lifetimeTokens"),
            summaryJson.strictLong("peakDailyTokens"),
            summaryJson.stringOrNull("peakDate")?.let(::parseDate),
            summaryJson.strictInt("activeDays"),
            summaryJson.strictInt("currentStreak"),
            summaryJson.strictInt("longestStreak"),
        )
        val daysJson = root.optJSONArray("days") ?: invalid()
        val days = buildList {
            for (index in 0 until daysJson.length()) {
                val day = daysJson.optJSONObject(index) ?: invalid()
                add(TokenUsageDay(
                    parseDate(day.stringOrNull("date") ?: invalid()),
                    day.strictLong("totalTokens"),
                    day.longOrNull("inputTokens"),
                    day.longOrNull("cachedInputTokens"),
                    day.longOrNull("outputTokens"),
                    day.longOrNull("reasoningTokens"),
                ))
            }
        }
        return TokenUsageSnapshot(
            schema,
            root.stringOrNull("generatedAtUtc") ?: invalid(),
            root.stringOrNull("sourceTimeZone") ?: invalid(),
            summary,
            days,
        )
    }

    fun serialize(value: TokenUsageSnapshot): String = JSONObject()
        .put("schemaVersion", value.schemaVersion)
        .put("generatedAtUtc", value.generatedAtUtc)
        .put("sourceTimeZone", value.sourceTimeZone)
        .put("summary", JSONObject()
            .put("todayTokens", value.summary.todayTokens)
            .put("last7DaysTokens", value.summary.last7DaysTokens)
            .put("last30DaysTokens", value.summary.last30DaysTokens)
            .put("lifetimeTokens", value.summary.lifetimeTokens)
            .put("peakDailyTokens", value.summary.peakDailyTokens)
            .put("peakDate", value.summary.peakDate?.toString() ?: JSONObject.NULL)
            .put("activeDays", value.summary.activeDays)
            .put("currentStreak", value.summary.currentStreak)
            .put("longestStreak", value.summary.longestStreak))
        .put("days", org.json.JSONArray().apply {
            value.days.forEach { day -> put(JSONObject()
                .put("date", day.date.toString())
                .put("totalTokens", day.totalTokens)
                .putNullable("inputTokens", day.inputTokens)
                .putNullable("cachedInputTokens", day.cachedInputTokens)
                .putNullable("outputTokens", day.outputTokens)
                .putNullable("reasoningTokens", day.reasoningTokens)) }
        }).toString()

    private fun parseDate(value: String): LocalDate = runCatching { LocalDate.parse(value) }.getOrElse { invalid() }
    private fun invalid(): Nothing = throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "Token 数据字段无效")
}

object TokenFormatter {
    fun format(value: Long): String {
        val positive = value.coerceAtLeast(0)
        return when {
            positive < 1_000 -> positive.toString()
            positive < 1_000_000 -> compact(positive, 1_000.0, "K")
            positive < 1_000_000_000 -> compact(positive, 1_000_000.0, "M")
            else -> compact(positive, 1_000_000_000.0, "B")
        }
    }

    private fun compact(value: Long, divisor: Double, suffix: String): String {
        val number = value / divisor
        val rendered = if (number >= 100 || number % 1.0 == 0.0) "%.0f".format(java.util.Locale.US, number) else "%.1f".format(java.util.Locale.US, number)
        return rendered + suffix
    }
}

object HeatmapBuckets {
    fun bucket(value: Long, nonZeroValues: List<Long>): Int {
        if (value <= 0L) return 0
        val sorted = nonZeroValues.filter { it > 0L }.sorted()
        if (sorted.isEmpty()) return 0
        val transformed = sorted.map { ln(it.toDouble() + 1.0) }
        val current = ln(value.toDouble() + 1.0)
        val q1 = transformed[(transformed.lastIndex * 0.25).toInt()]
        val q2 = transformed[(transformed.lastIndex * 0.50).toInt()]
        val q3 = transformed[(transformed.lastIndex * 0.75).toInt()]
        return when {
            current <= q1 -> 1
            current <= q2 -> 2
            current <= q3 -> 3
            else -> 4
        }
    }
}

private fun JSONObject.stringOrNull(key: String): String? = opt(key).takeIf { it is String } as String?
private fun JSONObject.longOrNull(key: String): Long? = opt(key).takeIf { it is Number }?.let { (it as Number).toLong() }
private fun JSONObject.strictLong(key: String): Long = longOrNull(key) ?: throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "Token 数据字段无效")
private fun JSONObject.strictInt(key: String): Int = strictLong(key).takeIf { it in Int.MIN_VALUE.toLong()..Int.MAX_VALUE.toLong() }?.toInt() ?: throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "Token 数据字段无效")
private fun JSONObject.putNullable(key: String, value: Any?): JSONObject = put(key, value ?: JSONObject.NULL)
