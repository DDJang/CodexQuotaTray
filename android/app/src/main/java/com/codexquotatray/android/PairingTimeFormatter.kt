package com.codexquotatray.android

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

internal object PairingTimeFormatter {
    fun format(
        valueMillis: Long,
        nowMillis: Long,
        zoneId: ZoneId,
        locale: Locale,
    ): String {
        val value = Instant.ofEpochMilli(valueMillis).atZone(zoneId)
        val now = Instant.ofEpochMilli(nowMillis).atZone(zoneId)
        val pattern = when (value.toLocalDate()) {
            now.toLocalDate() -> "'今天' HH:mm"
            now.toLocalDate().minusDays(1) -> "'昨天' HH:mm"
            else -> "MM-dd HH:mm"
        }
        return DateTimeFormatter.ofPattern(pattern, locale).format(value)
    }
}
