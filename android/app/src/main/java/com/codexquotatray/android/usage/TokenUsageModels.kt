package com.codexquotatray.android.usage

import org.json.JSONObject
import java.net.URI
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.time.LocalDate
import java.util.UUID
import kotlin.math.ln

data class TokenUsageDay(
    val date: LocalDate,
    val totalTokens: Long,
    val inputTokens: Long?,
    val cachedInputTokens: Long?,
    val outputTokens: Long?,
    val reasoningTokens: Long?,
)

enum class DataTransport { OPENAI, WINDOWS }

enum class TokenUsageScope { ACCOUNT, LOCAL }

data class TokenUsageSummary(
    val todayTokens: Long?,
    val last7DaysTokens: Long?,
    val last30DaysTokens: Long?,
    val lifetimeTokens: Long?,
    val peakDailyTokens: Long?,
    val peakDate: LocalDate?,
    val activeDays: Int?,
    val currentStreak: Int?,
    val longestStreak: Int?,
)

data class TokenUsageSnapshot(
    val schemaVersion: Int,
    val generatedAtUtc: String,
    val sourceTimeZone: String,
    val summary: TokenUsageSummary,
    val days: List<TokenUsageDay>,
    val transport: DataTransport = DataTransport.WINDOWS,
    val scope: TokenUsageScope = TokenUsageScope.LOCAL,
    val source: String? = null,
)

data class TokenSyncPairing(
    val deviceId: String,
    val pairingSecret: String,
    val lastKnownHost: String,
    val port: Int,
    val displayName: String? = null,
    val lastSyncUtc: String? = null,
    val lastSuccessfulSyncAtMillis: Long? = null,
) {
    constructor(host: String, port: Int, secret: String) : this("", secret, host, port)

    val host: String get() = lastKnownHost
    val secret: String get() = pairingSecret
    val url: String get() = "http://$lastKnownHost:$port/v1/token-usage"
}

/**
 * QR pairings have a stable Windows device id. Older manual pairings do not,
 * so retain a conservative endpoint identity for their private local cache.
 * Neither form contains the pairing secret.
 */
internal fun TokenSyncPairing.cacheIdentity(): String = deviceId
    .trim()
    .takeIf { it.isNotEmpty() }
    ?.lowercase()
    ?: "legacy:$lastKnownHost:$port"

/** Process-local single-flight key covering the complete immutable pairing configuration. */
internal fun TokenSyncPairing.singleFlightIdentity(): String {
    val digest = MessageDigest.getInstance("SHA-256")
    listOf(deviceId.lowercase(), lastKnownHost, port.toString(), pairingSecret).forEach { value ->
        val bytes = value.toByteArray(Charsets.UTF_8)
        digest.update(ByteBuffer.allocate(Int.SIZE_BYTES).putInt(bytes.size).array())
        digest.update(bytes)
    }
    return digest.digest().joinToString(separator = "") { byte -> "%02x".format(byte.toInt() and 0xff) }
}

/** Ignores mutable sync metadata while detecting a pairing replacement. */
internal fun TokenSyncPairing.matchesConfiguration(other: TokenSyncPairing): Boolean =
    deviceId.equals(other.deviceId, ignoreCase = true) &&
        pairingSecret == other.pairingSecret &&
        lastKnownHost == other.lastKnownHost &&
        port == other.port

object TokenSyncEndpoint {
    const val ServiceType = "_codexquota._tcp"

    fun parsePairingUri(raw: String): TokenSyncPairing {
        val uri = runCatching { URI(raw.trim()) }.getOrElse { throw IllegalArgumentException("配对信息格式无效") }
        require(uri.scheme.equals("codexquota", ignoreCase = true) && uri.host.equals("pair", ignoreCase = true)) {
            "配对信息格式无效"
        }
        require(uri.rawPath.isNullOrEmpty() && uri.rawFragment == null && uri.rawUserInfo == null && uri.port == -1) {
            "配对信息格式无效"
        }
        val rawParts = uri.rawQuery.orEmpty().split('&').filter { it.isNotEmpty() }
        val query = rawParts.mapNotNull { part ->
            val separator = part.indexOf('=')
            if (separator <= 0) null else java.net.URLDecoder.decode(part.substring(0, separator), "UTF-8") to
                java.net.URLDecoder.decode(part.substring(separator + 1), "UTF-8")
        }.toMap().also { values -> require(values.size == rawParts.size) { "配对信息格式无效" } }
        require(!query["deviceId"].isNullOrBlank()) { "Windows 设备标识缺失" }
        return validated(
            query["deviceId"].orEmpty(),
            query["host"].orEmpty(),
            query["port"]?.toIntOrNull() ?: throw IllegalArgumentException("端口无效"),
            query["token"].orEmpty(),
            query["name"],
        )
    }

    fun parseManual(address: String, secret: String): TokenSyncPairing {
        val trimmed = address.trim()
        val separator = trimmed.lastIndexOf(':')
        val host = if (separator > 0) trimmed.substring(0, separator) else trimmed
        val port = if (separator > 0) trimmed.substring(separator + 1).toIntOrNull() ?: throw IllegalArgumentException("端口无效") else 43821
        return validated(host, port, secret.trim())
    }

    fun validated(host: String, port: Int, secret: String): TokenSyncPairing {
        return validated("", host, port, secret)
    }

    fun validated(
        deviceId: String,
        host: String,
        port: Int,
        secret: String,
        displayName: String? = null,
        lastSyncUtc: String? = null,
        lastSuccessfulSyncAtMillis: Long? = null,
    ): TokenSyncPairing {
        require(deviceId.isBlank() || isValidDeviceId(deviceId)) { "Windows 设备标识无效" }
        require(isPrivateIpv4(host)) { "仅允许私人局域网 IPv4 地址" }
        require(port in 1..65535) { "端口无效" }
        require(secret.isNotBlank()) { "配对密钥不能为空" }
        return TokenSyncPairing(
            deviceId.trim(),
            secret,
            host,
            port,
            displayName?.takeIf { it.isNotBlank() },
            lastSyncUtc,
            lastSuccessfulSyncAtMillis,
        )
    }

    fun isValidDeviceId(value: String): Boolean = runCatching {
        UUID.fromString(value).toString().equals(value, ignoreCase = true)
    }.getOrDefault(false)

    fun isDiscoveryEnabled(pairing: TokenSyncPairing): Boolean = isValidDeviceId(pairing.deviceId)

    fun shouldDiscover(failure: TokenUsageFailureKind, pairing: TokenSyncPairing): Boolean =
        failure == TokenUsageFailureKind.OFFLINE && isDiscoveryEnabled(pairing)

    data class TokenSyncDiscoveryCandidate(val deviceId: String, val host: String, val port: Int, val displayName: String?)

    fun chooseDiscoveryCandidate(
        candidates: Iterable<TokenSyncDiscoveryCandidate>,
        deviceId: String,
    ): TokenSyncDiscoveryCandidate? = candidates.firstOrNull { it.deviceId.equals(deviceId, ignoreCase = true) }

    fun updateHost(pairing: TokenSyncPairing, candidate: TokenSyncDiscoveryCandidate): TokenSyncPairing =
        validated(
            pairing.deviceId,
            candidate.host,
            candidate.port,
            pairing.pairingSecret,
            candidate.displayName ?: pairing.displayName,
            pairing.lastSyncUtc,
            pairing.lastSuccessfulSyncAtMillis,
        )

    fun markSynced(
        pairing: TokenSyncPairing,
        snapshot: TokenUsageSnapshot,
        nowMillis: Long = System.currentTimeMillis(),
    ): TokenSyncPairing = pairing.copy(
        lastSyncUtc = snapshot.generatedAtUtc,
        lastSuccessfulSyncAtMillis = nowMillis,
    )

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
            summaryJson.longOrNull("todayTokens"),
            summaryJson.longOrNull("last7DaysTokens"),
            summaryJson.longOrNull("last30DaysTokens"),
            summaryJson.longOrNull("lifetimeTokens"),
            summaryJson.longOrNull("peakDailyTokens"),
            summaryJson.stringOrNull("peakDate")?.let(::parseDate),
            summaryJson.intOrNull("activeDays"),
            summaryJson.intOrNull("currentStreak"),
            summaryJson.intOrNull("longestStreak"),
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
            transport = if (root.has("transport")) root.requiredEnum("transport") else DataTransport.WINDOWS,
            scope = if (!root.has("scope") && !root.has("source")) {
                TokenUsageScope.LOCAL
            } else {
                root.requiredEnum("scope")
            },
            source = root.stringOrNull("source"),
        )
    }

    fun serialize(value: TokenUsageSnapshot): String = JSONObject()
        .put("schemaVersion", value.schemaVersion)
        .put("generatedAtUtc", value.generatedAtUtc)
        .put("sourceTimeZone", value.sourceTimeZone)
        .put("transport", value.transport.name)
        .put("scope", value.scope.name)
        .put("source", value.source ?: JSONObject.NULL)
        .put("summary", JSONObject()
            .putNullable("todayTokens", value.summary.todayTokens)
            .putNullable("last7DaysTokens", value.summary.last7DaysTokens)
            .putNullable("last30DaysTokens", value.summary.last30DaysTokens)
            .putNullable("lifetimeTokens", value.summary.lifetimeTokens)
            .putNullable("peakDailyTokens", value.summary.peakDailyTokens)
            .put("peakDate", value.summary.peakDate?.toString() ?: JSONObject.NULL)
            .putNullable("activeDays", value.summary.activeDays)
            .putNullable("currentStreak", value.summary.currentStreak)
            .putNullable("longestStreak", value.summary.longestStreak))
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
private fun JSONObject.intOrNull(key: String): Int? = longOrNull(key)
    ?.takeIf { it in Int.MIN_VALUE.toLong()..Int.MAX_VALUE.toLong() }
    ?.toInt()
private inline fun <reified T : Enum<T>> JSONObject.requiredEnum(key: String): T {
    val raw = stringOrNull(key) ?: throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "Token 数据字段无效")
    return enumValues<T>().firstOrNull { it.name.equals(raw, ignoreCase = true) }
        ?: throw TokenUsageException(TokenUsageFailureKind.INVALID_RESPONSE, "Token 数据字段无效")
}
private fun JSONObject.putNullable(key: String, value: Any?): JSONObject = put(key, value ?: JSONObject.NULL)
