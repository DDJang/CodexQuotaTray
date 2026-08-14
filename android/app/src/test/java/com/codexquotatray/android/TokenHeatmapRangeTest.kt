package com.codexquotatray.android

import com.codexquotatray.android.usage.TokenUsageDay
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.DayOfWeek
import java.time.LocalDate

class TokenHeatmapRangeTest {
    private val today = LocalDate.of(2026, 8, 10)

    @Test
    fun noUsageShowsTheMostRecentEightWeekColumns() {
        val range = tokenHeatmapRange(emptyList(), today)

        assertEquals(LocalDate.of(2026, 6, 21), range.start)
        assertEquals(today, range.end)
        assertEquals(8, range.columnCount)
    }

    @Test
    fun recentUsageStillShowsAtLeastEightWeeks() {
        val range = tokenHeatmapRange(listOf(day(LocalDate.of(2026, 7, 22))), today)

        assertEquals(LocalDate.of(2026, 6, 21), range.start)
        assertEquals(8, range.columnCount)
    }

    @Test
    fun olderUsageStartsAtTheFirstUsageWeek() {
        val range = tokenHeatmapRange(listOf(day(LocalDate.of(2026, 5, 12))), today)

        assertEquals(LocalDate.of(2026, 5, 10), range.start)
        assertEquals(DayOfWeek.SUNDAY, range.start.dayOfWeek)
    }

    @Test
    fun historyIsClampedToTheLast365DaysOnACompleteWeekBoundary() {
        val range = tokenHeatmapRange(listOf(day(LocalDate.of(2024, 1, 1))), today)

        assertEquals(LocalDate.of(2025, 8, 17), range.start)
        assertTrue(range.dayCount <= 365)
        assertEquals(DayOfWeek.SUNDAY, range.start.dayOfWeek)
    }

    @Test
    fun selectedDayTooltipUsesExactTokenCountAndIsoDate() {
        assertEquals("12 Token\n2026-08-10", formatHeatmapSelection(day(today, 12)))
        assertEquals("12 Token\n2025-08-10", formatHeatmapSelection(day(today.minusYears(1), 12)))
    }

    private fun day(date: LocalDate, tokens: Long = 1) = TokenUsageDay(date, tokens, null, null, null, null)
}
