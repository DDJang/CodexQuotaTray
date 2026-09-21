package com.codexquotatray.android

import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.TimeZone

internal object UpdatedAtFormatter {
    private const val EXPIRED_AFTER_MILLIS = 7L * 24L * 60L * 60L * 1_000L

    fun formatValue(
        updatedAtMillis: Long,
        nowMillis: Long = System.currentTimeMillis(),
        locale: Locale = Locale.getDefault(),
        timeZone: TimeZone = TimeZone.getDefault(),
    ): String {
        val updatedAt = Calendar.getInstance(timeZone).apply { timeInMillis = updatedAtMillis }
        val now = Calendar.getInstance(timeZone).apply { timeInMillis = nowMillis }
        val isToday = updatedAt.get(Calendar.ERA) == now.get(Calendar.ERA) &&
            updatedAt.get(Calendar.YEAR) == now.get(Calendar.YEAR) &&
            updatedAt.get(Calendar.DAY_OF_YEAR) == now.get(Calendar.DAY_OF_YEAR)
        val value = SimpleDateFormat(if (isToday) "HH:mm" else "M月d日", locale).apply {
            this.timeZone = timeZone
        }.format(Date(updatedAtMillis))
        return if (isExpired(updatedAtMillis, nowMillis)) "$value · 已过期" else value
    }

    fun isExpired(updatedAtMillis: Long, nowMillis: Long = System.currentTimeMillis()): Boolean =
        nowMillis - updatedAtMillis > EXPIRED_AFTER_MILLIS
}
