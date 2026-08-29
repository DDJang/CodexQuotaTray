package com.codexquotatray.android

import android.content.Context
import java.text.ParsePosition
import java.text.SimpleDateFormat
import java.util.ArrayDeque
import java.util.Date
import java.util.HashMap
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

class AppLogStore(
    context: Context,
    private val nowMillis: () -> Long = { System.currentTimeMillis() },
) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )
    private val appLogCacheKey = context.applicationContext.filesDir.absolutePath
    private val lanStore = lanStores.computeIfAbsent(context.applicationContext.filesDir.absolutePath) {
        LanDiagnosticFileStore(
            context.applicationContext.filesDir.resolve(LAN_DIRECTORY),
            LegacyLanLogSource { clear ->
                val currentSlot = preferences.getInt(KEY_LAN_SLOT, 0)
                val value = (1..AppLanLogRetention.SLOT_COUNT)
                    .map { offset ->
                        preferences.getString(
                            lanSlotKey((currentSlot + offset) % AppLanLogRetention.SLOT_COUNT),
                            "",
                        ).orEmpty()
                    }
                    .filter(String::isNotBlank)
                    .joinToString("\n")
                if (clear) {
                    preferences.edit()
                        .remove(KEY_LAN_SLOT)
                        .also { editor ->
                            repeat(AppLanLogRetention.SLOT_COUNT) { slot -> editor.remove(lanSlotKey(slot)) }
                        }
                        .commit()
                }
                value
            },
        )
    }

    fun append(message: String, level: String = "INFO") {
        synchronized(AppLogStore::class.java) {
            val now = nowMillis()
            val entries = cachedEntries()
            entries.prune(now)
            val line = "${AppLogRetention.formatTimestamp(now)} [${level.uppercase(Locale.ROOT)}] " +
                AppLogSanitizer.sanitize(message)
            entries.append(line, now)
            persist(entries.lines())
        }
    }

    fun appendLan(message: String, level: String = "INFO") {
        val now = nowMillis()
        val line = "${AppLogRetention.formatTimestamp(now)} [${level.uppercase(Locale.ROOT)}] " +
            AppLogSanitizer.sanitizeLan(message)
        LanDiagnosticWriter.append(lanStore, line)
    }

    fun read(): String = synchronized(AppLogStore::class.java) {
        val entries = cachedEntries()
        val changed = entries.prune(nowMillis())
        val lines = entries.lines()
        if (changed) {
            persist(lines)
        }
        lines.joinToString("\n").takeIf(String::isNotBlank) ?: "暂无日志"
    }

    fun readLan(): String = runCatching { LanDiagnosticWriter.read(lanStore) }
        .getOrDefault("")
        .takeIf(String::isNotBlank)
        ?: "暂无日志"

    fun clear() {
        synchronized(AppLogStore::class.java) {
            appLogCaches.remove(appLogCacheKey)
            preferences.edit()
                .remove(KEY_ENTRIES)
                .apply()
        }
        LanDiagnosticWriter.clear(lanStore)
    }

    private fun loadEntries(): List<String> = preferences.getString(KEY_ENTRIES, "")
        .orEmpty()
        .lineSequence()
        .filter(String::isNotBlank)
        .toList()

    private fun cachedEntries(): AppLogBuffer = appLogCaches.getOrPut(appLogCacheKey) {
        AppLogBuffer(loadEntries())
    }

    private fun persist(entries: List<String>) {
        preferences.edit().putString(KEY_ENTRIES, entries.joinToString("\n")).apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "app_logs"
        private const val KEY_ENTRIES = "entries"
        private const val KEY_LAN_SLOT = "lan_slot"
        private const val LAN_DIRECTORY = "lan-diagnostics"
        private val appLogCaches = HashMap<String, AppLogBuffer>()
        private val lanStores = ConcurrentHashMap<String, LanDiagnosticFileStore>()

        private fun lanSlotKey(slot: Int): String = "lan_entries_$slot"

        fun record(context: Context, message: String, level: String = "INFO") {
            AppLogStore(context).append(message, level)
        }

        fun recordLan(context: Context, message: String, level: String = "INFO") {
            AppLogStore(context).appendLan(message, level)
        }
    }
}

internal object AppLogRetention {
    const val MAX_ENTRIES = 120
    internal const val RETENTION_MILLIS = 7L * 24L * 60L * 60L * 1_000L
    private const val TIMESTAMP_LENGTH = 19
    private const val TIMESTAMP_PATTERN = "yyyy-MM-dd HH:mm:ss"
    private data class TimestampFormatter(
        val locale: Locale,
        val formatter: SimpleDateFormat,
    )
    private val timestampFormatter = ThreadLocal<TimestampFormatter>()

    fun prune(entries: List<String>, nowMillis: Long): List<String> {
        val cutoff = nowMillis - RETENTION_MILLIS
        return entries
            .filter { line ->
                val timestamp = parseTimestamp(line)
                timestamp == null || timestamp >= cutoff
            }
            .takeLast(MAX_ENTRIES)
    }

    fun formatTimestamp(millis: Long): String = formatter().format(Date(millis))

    internal fun parseTimestamp(line: String): Long? {
        if (line.length < TIMESTAMP_LENGTH) return null
        val position = ParsePosition(0)
        val date = formatter().parse(
            line.substring(0, TIMESTAMP_LENGTH),
            position,
        ) ?: return null
        return date.time.takeIf { position.index == TIMESTAMP_LENGTH }
    }

    private fun formatter(): SimpleDateFormat {
        val locale = Locale.getDefault()
        val cached = timestampFormatter.get()
        if (cached != null && cached.locale == locale) return cached.formatter
        return SimpleDateFormat(TIMESTAMP_PATTERN, locale).also { formatter ->
            timestampFormatter.set(TimestampFormatter(locale, formatter))
        }
    }
}

internal class AppLogBuffer(
    lines: List<String>,
    timestampParser: (String) -> Long? = AppLogRetention::parseTimestamp,
) {
    private data class Entry(val line: String, val timestampMillis: Long?)

    private val entries = ArrayDeque<Entry>(lines.size)

    init {
        lines.forEach { line -> entries.addLast(Entry(line, timestampParser(line))) }
    }

    fun append(line: String, timestampMillis: Long) {
        entries.addLast(Entry(line, timestampMillis))
        trimToLimit()
    }

    fun prune(nowMillis: Long): Boolean {
        val cutoff = nowMillis - AppLogRetention.RETENTION_MILLIS
        var changed = false
        val iterator = entries.iterator()
        while (iterator.hasNext()) {
            val timestampMillis = iterator.next().timestampMillis
            if (timestampMillis != null && timestampMillis < cutoff) {
                iterator.remove()
                changed = true
            }
        }
        return trimToLimit() || changed
    }

    fun lines(): List<String> = entries.map(Entry::line)

    private fun trimToLimit(): Boolean {
        var changed = false
        while (entries.size > AppLogRetention.MAX_ENTRIES) {
            entries.removeFirst()
            changed = true
        }
        return changed
    }
}

internal object AppLanLogRetention {
    const val SLOT_COUNT = 3
    const val MAX_SLOT_BYTES = 1_024 * 1_024

    fun utf8Size(value: String): Int = value.toByteArray(Charsets.UTF_8).size
}

internal object AppLogSanitizer {
    private val jsonSecretPattern = Regex(
        """(?i)(\"?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|pairing[_-]?secret|pairingsecret|authorization|cookie|device[_-]?code|token)\"?\s*:\s*\")([^\"]*)(\")""",
    )
    private val authorizationHeaderPattern = Regex(
        """(?i)(\bAuthorization\b\s*[:=]\s*)(?:Bearer\s+)?[^\s,;}\]]+""",
    )
    private val secretPattern = Regex(
        """(?i)(\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|pairing[_-]?secret|pairingsecret|pairing\s+secret|client[_-]?secret|password|token)\b\s*[:=]\s*)(?:\"[^\"]*\"|[^\s,;}\]]+)""",
    )
    private val cookiePattern = Regex(
        """(?i)(\bCookie\b\s*[:=]\s*)[^\r\n]+""",
    )
    private val deviceCodePattern = Regex(
        """(?i)(\bdevice[ _-]*code\b|登录码)\s*[:：=]?\s*[A-Za-z0-9][A-Za-z0-9_-]*""",
    )

    fun sanitize(message: String): String = sanitizeInternal(message, MAX_MESSAGE_LENGTH)

    fun sanitizeLan(message: String): String = sanitizeInternal(message, MAX_LAN_MESSAGE_LENGTH)

    private fun sanitizeInternal(message: String, maximumLength: Int): String = message
        .replace(jsonSecretPattern) { "${it.groupValues[1]}[已隐藏]${it.groupValues[3]}" }
        .replace(authorizationHeaderPattern) { "${it.groupValues[1]}[已隐藏]" }
        .replace(secretPattern) { redactSecret(it) }
        .replace(cookiePattern) { "${it.groupValues[1]}[已隐藏]" }
        .replace(deviceCodePattern) { redactDeviceCode(it) }
        .replace(Regex("\\s+"), " ")
        .trim()
        .take(maximumLength)

    private fun redactSecret(match: MatchResult): String =
        match.value.substringBeforeAny(':', '=') + "=[已隐藏]"

    private fun redactDeviceCode(match: MatchResult): String =
        match.value.substringBeforeAny(':', '：') + "：[已隐藏]"

    private fun String.substringBeforeAny(vararg delimiters: Char): String =
       delimiters.map { indexOf(it) }
           .filter { it >= 0 }
           .minOrNull()
           ?.let { substring(0, it) }
           ?: this

    private const val MAX_MESSAGE_LENGTH = 240
    private const val MAX_LAN_MESSAGE_LENGTH = 512
}
