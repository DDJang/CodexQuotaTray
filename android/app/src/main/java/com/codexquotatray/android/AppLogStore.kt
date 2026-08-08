package com.codexquotatray.android

import android.content.Context
import java.text.ParsePosition
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class AppLogStore(
    context: Context,
    private val nowMillis: () -> Long = { System.currentTimeMillis() },
) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun append(message: String, level: String = "INFO") {
        synchronized(AppLogStore::class.java) {
            val now = nowMillis()
            val previous = AppLogRetention.prune(loadEntries(), now)
            val line = "${AppLogRetention.formatTimestamp(now)} [${level.uppercase(Locale.ROOT)}] " +
                AppLogSanitizer.sanitize(message)
            persist((previous + line).takeLast(AppLogRetention.MAX_ENTRIES))
        }
    }

    fun read(): String = synchronized(AppLogStore::class.java) {
        val entries = AppLogRetention.prune(loadEntries(), nowMillis())
        persist(entries)
        entries.joinToString("\n").takeIf(String::isNotBlank) ?: "暂无日志"
    }

    fun clear() {
        synchronized(AppLogStore::class.java) {
            preferences.edit().remove(KEY_ENTRIES).apply()
        }
    }

    private fun loadEntries(): List<String> = preferences.getString(KEY_ENTRIES, "")
        .orEmpty()
        .lineSequence()
        .filter(String::isNotBlank)
        .toList()

    private fun persist(entries: List<String>) {
        preferences.edit().putString(KEY_ENTRIES, entries.joinToString("\n")).apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "app_logs"
        private const val KEY_ENTRIES = "entries"

        fun record(context: Context, message: String, level: String = "INFO") {
            AppLogStore(context).append(message, level)
        }
    }
}

internal object AppLogRetention {
    const val MAX_ENTRIES = 120
    private const val RETENTION_MILLIS = 7L * 24L * 60L * 60L * 1_000L
    private const val TIMESTAMP_LENGTH = 19
    private const val TIMESTAMP_PATTERN = "yyyy-MM-dd HH:mm:ss"

    fun prune(entries: List<String>, nowMillis: Long): List<String> {
        val cutoff = nowMillis - RETENTION_MILLIS
        return entries
            .filter { line ->
                val timestamp = parseTimestamp(line)
                timestamp == null || timestamp >= cutoff
            }
            .takeLast(MAX_ENTRIES)
    }

    fun formatTimestamp(millis: Long): String = SimpleDateFormat(
        TIMESTAMP_PATTERN,
        Locale.getDefault(),
    ).format(Date(millis))

    private fun parseTimestamp(line: String): Long? {
        if (line.length < TIMESTAMP_LENGTH) return null
        val position = ParsePosition(0)
        val date = SimpleDateFormat(TIMESTAMP_PATTERN, Locale.getDefault()).parse(
            line.substring(0, TIMESTAMP_LENGTH),
            position,
        ) ?: return null
        return date.time.takeIf { position.index == TIMESTAMP_LENGTH }
    }
}

internal object AppLogSanitizer {
    private val secretPattern = Regex(
        "(?i)(access[_-]?token|refresh[_-]?token|id[_-]?token|authorization)\\s*[:=]\\s*[^\\s,;]+",
    )
    private val deviceCodePattern = Regex("(?i)(device\\s*code|登录码)\\s*[:：]?\\s*[^\\s,;]+")

    fun sanitize(message: String): String = message
        .replace(secretPattern) { redactSecret(it) }
        .replace(deviceCodePattern) { redactDeviceCode(it) }
        .replace(Regex("\\s+"), " ")
        .trim()
        .take(MAX_MESSAGE_LENGTH)

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
}
