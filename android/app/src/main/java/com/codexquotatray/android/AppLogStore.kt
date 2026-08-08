package com.codexquotatray.android

import android.content.Context
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class AppLogStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

   fun append(message: String, level: String = "INFO") {
        synchronized(AppLogStore::class.java) {
            val line = "${timestamp()} [${level.uppercase(Locale.ROOT)}] ${AppLogSanitizer.sanitize(message)}"
            val previous = preferences.getString(KEY_ENTRIES, "")
                .orEmpty()
                .lineSequence()
                .filter(String::isNotBlank)
                .toList()
            val entries = (previous + line).takeLast(MAX_ENTRIES)
            preferences.edit().putString(KEY_ENTRIES, entries.joinToString("\n")).apply()
        }
   }

    fun read(): String = preferences.getString(KEY_ENTRIES, "")
        .orEmpty()
        .takeIf(String::isNotBlank)
        ?: "暂无日志"

    private fun timestamp(): String = SimpleDateFormat(
        "yyyy-MM-dd HH:mm:ss",
        Locale.getDefault(),
    ).format(Date())

    companion object {
        private const val PREFERENCES_NAME = "app_logs"
        private const val KEY_ENTRIES = "entries"
        private const val MAX_ENTRIES = 120

        fun record(context: Context, message: String, level: String = "INFO") {
            AppLogStore(context).append(message, level)
        }
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
