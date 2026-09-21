package com.codexquotatray.android

import java.time.Instant
import java.util.Locale
import java.util.TimeZone
import org.junit.Assert.assertEquals
import org.junit.Test

class UpdatedAtFormatterTest {
    private val shanghai = TimeZone.getTimeZone("Asia/Shanghai")

    @Test
    fun todayUsesClockTime() {
        assertEquals(
            "09:05",
            UpdatedAtFormatter.formatValue(
                updatedAtMillis = Instant.parse("2026-09-21T01:05:00Z").toEpochMilli(),
                nowMillis = Instant.parse("2026-09-21T12:00:00Z").toEpochMilli(),
                locale = Locale.CHINA,
                timeZone = shanghai,
            ),
        )
    }

    @Test
    fun earlierLocalDateUsesMonthAndDayWithoutClockTime() {
        assertEquals(
            "9月20日",
            UpdatedAtFormatter.formatValue(
                updatedAtMillis = Instant.parse("2026-09-20T15:59:00Z").toEpochMilli(),
                nowMillis = Instant.parse("2026-09-20T16:01:00Z").toEpochMilli(),
                locale = Locale.CHINA,
                timeZone = shanghai,
            ),
        )
    }

    @Test
    fun ageBeyondSevenDaysIsExpiredButTheBoundaryIsNot() {
        val updatedAt = Instant.parse("2026-09-01T00:00:00Z").toEpochMilli()
        val sevenDays = 7L * 24L * 60L * 60L * 1_000L

        assertEquals(
            "9月1日",
            UpdatedAtFormatter.formatValue(updatedAt, updatedAt + sevenDays, Locale.CHINA, shanghai),
        )
        assertEquals(
            "9月1日 · 已过期",
            UpdatedAtFormatter.formatValue(updatedAt, updatedAt + sevenDays + 1L, Locale.CHINA, shanghai),
        )
    }
}
