package com.codexquotatray.android

import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AppLogRetentionTest {
    @Test
    fun entriesOlderThanSevenDaysAreRemoved() {
        val now = parse("2026-08-08 12:00:00")
        val old = AppLogRetention.formatTimestamp(now - SEVEN_DAYS_MILLIS - 1_000L) + " old"
        val fresh = AppLogRetention.formatTimestamp(now - 1_000L) + " fresh"

        val retained = AppLogRetention.prune(listOf(old, fresh), now)

        assertEquals(listOf(fresh), retained)
    }

    @Test
    fun entryExactlyAtSevenDayBoundaryIsRetained() {
        val now = parse("2026-08-08 12:00:00")
        val boundary = AppLogRetention.formatTimestamp(now - SEVEN_DAYS_MILLIS) + " boundary"

        val retained = AppLogRetention.prune(listOf(boundary), now)

        assertEquals(listOf(boundary), retained)
    }

    @Test
    fun retentionStillKeepsOnlyLast120Entries() {
        val now = parse("2026-08-08 12:00:00")
        val entries = (1..121).map {
            "${AppLogRetention.formatTimestamp(now)} entry-$it"
        }

        val retained = AppLogRetention.prune(entries, now)

        assertEquals(AppLogRetention.MAX_ENTRIES, retained.size)
        assertFalse(retained.first().contains("entry-1"))
        assertTrue(retained.last().contains("entry-121"))
    }

    @Test
    fun cachedBufferParsesExistingTimestampsOnlyOnce() {
        val now = parse("2026-08-08 12:00:00")
        val existing = listOf(
            "${AppLogRetention.formatTimestamp(now - 1_000L)} first",
            "${AppLogRetention.formatTimestamp(now)} second",
        )
        var parseCount = 0
        val buffer = AppLogBuffer(existing) { line ->
            parseCount += 1
            AppLogRetention.parseTimestamp(line)
        }

        buffer.prune(now)
        buffer.prune(now + 1_000L)
        buffer.append("${AppLogRetention.formatTimestamp(now + 2_000L)} third", now + 2_000L)
        buffer.prune(now + 2_000L)

        assertEquals(existing.size, parseCount)
        assertEquals(3, buffer.lines().size)
    }

    @Test
    fun cachedBufferKeepsBoundaryAndRemovesOnlyOlderEntries() {
        val now = parse("2026-08-08 12:00:00")
        val old = "${AppLogRetention.formatTimestamp(now - SEVEN_DAYS_MILLIS - 1_000L)} old"
        val boundary = "${AppLogRetention.formatTimestamp(now - SEVEN_DAYS_MILLIS)} boundary"
        val buffer = AppLogBuffer(listOf(old, boundary))

        buffer.prune(now)

        assertEquals(listOf(boundary), buffer.lines())
    }

    @Test
    fun cachedBufferKeepsMalformedLinesAndLast120Entries() {
        val now = parse("2026-08-08 12:00:00")
        val buffer = AppLogBuffer(listOf("legacy malformed line"))

        repeat(AppLogRetention.MAX_ENTRIES) { index ->
            buffer.append("${AppLogRetention.formatTimestamp(now)} entry-$index", now)
        }
        buffer.prune(now)

        assertEquals(AppLogRetention.MAX_ENTRIES, buffer.lines().size)
        assertFalse(buffer.lines().contains("legacy malformed line"))
        assertTrue(buffer.lines().last().endsWith("entry-119"))
    }

    @Test
    fun formatterRefreshesWhenDefaultTimeZoneChanges() {
        val originalLocale = Locale.getDefault()
        val originalTimeZone = TimeZone.getDefault()
        try {
            Locale.setDefault(Locale.US)
            TimeZone.setDefault(TimeZone.getTimeZone("GMT"))
            assertEquals("1970-01-01 00:00:00", AppLogRetention.formatTimestamp(0L))

            TimeZone.setDefault(TimeZone.getTimeZone("GMT+08:00"))

            val shifted = AppLogRetention.formatTimestamp(0L)
            assertEquals("1970-01-01 08:00:00", shifted)
            assertEquals(0L, AppLogRetention.parseTimestamp("$shifted entry"))
        } finally {
            TimeZone.setDefault(originalTimeZone)
            Locale.setDefault(originalLocale)
        }
    }

    private fun parse(value: String): Long = SimpleDateFormat(
        "yyyy-MM-dd HH:mm:ss",
        Locale.getDefault(),
    ).parse(value)!!.time

    companion object {
        private const val SEVEN_DAYS_MILLIS = 7L * 24L * 60L * 60L * 1_000L
    }
}
