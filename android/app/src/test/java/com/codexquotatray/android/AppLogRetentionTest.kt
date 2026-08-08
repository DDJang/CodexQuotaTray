package com.codexquotatray.android

import java.text.SimpleDateFormat
import java.util.Locale
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

    private fun parse(value: String): Long = SimpleDateFormat(
        "yyyy-MM-dd HH:mm:ss",
        Locale.getDefault(),
    ).parse(value)!!.time

    companion object {
        private const val SEVEN_DAYS_MILLIS = 7L * 24L * 60L * 60L * 1_000L
    }
}
