package com.codexquotatray.android

import com.codexquotatray.android.usage.TokenUsageDay
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.temporal.TemporalAdjusters

class TokenHeatmapRangeTest {
    private val today = LocalDate.of(2026, 8, 10)

    @Test
    fun rangeAlwaysUsesTheMostRecentThirteenWeeks() {
        listOf(
            LocalDate.of(2026, 8, 9),
            LocalDate.of(2026, 8, 10),
            LocalDate.of(2026, 8, 12),
            LocalDate.of(2026, 8, 15),
        ).forEach { date ->
            val range = tokenHeatmapRange(date)
            val expectedStart = date.with(TemporalAdjusters.previousOrSame(DayOfWeek.SUNDAY)).minusWeeks(12)

            assertEquals(expectedStart, range.start)
            assertEquals(date, range.end)
            assertEquals(13, range.columnCount)
            assertTrue(range.dayCount in 85..91)
        }
    }

    @Test
    fun selectedDayTooltipUsesExactTokenCountAndIsoDate() {
        assertEquals("12 Token\n2026-08-10", formatHeatmapSelection(day(today, 12)))
        assertEquals("12 Token\n2025-08-10", formatHeatmapSelection(day(today.minusYears(1), 12)))
    }

    private fun day(date: LocalDate, tokens: Long = 1) = TokenUsageDay(date, tokens, null, null, null, null)
}
