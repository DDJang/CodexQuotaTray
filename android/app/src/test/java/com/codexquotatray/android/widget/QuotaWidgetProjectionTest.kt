package com.codexquotatray.android.widget

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaWidgetProjectionTest {
    @Test
    fun standardFiveHourAndSevenDayWindowsProjectToTwoRows() {
        val projection = QuotaWidgetProjection.fromResult(
            result = result(
                window("primary", 300, 72, 1_900_000_000L),
                window("secondary", 10_080, 48, 1_900_600_000L),
            ),
            updatedAtMillis = 1_700_000_000_000L,
        )

        assertEquals("5 小时", projection.primary?.title)
        assertEquals(72, projection.primary?.remainingPercent)
        assertEquals(300L, projection.primary?.windowDurationMins)
        assertEquals("7 天", projection.secondary?.title)
        assertEquals(48, projection.secondary?.remainingPercent)
        assertEquals(2, projection.windows.size)
    }

    @Test
    fun oneWindowAndUnknownRemainingAreRepresentedWithoutFabricatedZero() {
        val projection = QuotaWidgetProjection.fromResult(
            result("primary", window("primary", 300, null, null)),
            updatedAtMillis = 1_700_000_000_000L,
        )

        assertEquals(1, projection.windows.size)
        assertNull(projection.primary?.remainingPercent)
        assertNull(projection.primary?.resetsAt)
        assertNull(projection.secondary)
    }

    @Test
    fun codecRoundTripsAndMalformedInputFailsClosed() {
        val original = QuotaWidgetProjection(
            planType = "Plus",
            updatedAtMillis = 1_700_000_000_000L,
            primary = QuotaWidgetWindow("5 小时", 72, 1_900_000_000L, 300L),
            secondary = null,
        )

        assertEquals(original, QuotaWidgetProjectionCodec.decode(QuotaWidgetProjectionCodec.encode(original)))
        assertNull(QuotaWidgetProjectionCodec.decode("{not-json"))
        assertNull(QuotaWidgetProjectionCodec.decode("{}"))
    }

    @Test
    fun displayFormattingContainsUpdatedTimeAndResetInformation() {
        assertTrue(QuotaWidgetDisplayFormatter.formatUpdatedAt(1_700_000_000_000L).startsWith("更新于 "))
        assertTrue(
            QuotaWidgetDisplayFormatter.formatResetAt(
                resetsAtSeconds = 1_700_003_600L,
                nowMillis = 1_700_000_000_000L,
            ).contains("后重置"),
        )
    }

    @Test
    fun emptyAndUnavailableResultsProduceNoDataProjection() {
        val empty = QuotaWidgetProjection.fromResult(
            result = result(),
            updatedAtMillis = 1_700_000_000_000L,
        )
        val unavailable = QuotaWidgetProjection.fromResult(
            result = result().copy(quotaState = "unavailable"),
            updatedAtMillis = 1_700_000_000_000L,
        )

        assertTrue(empty.windows.isEmpty())
        assertTrue(unavailable.windows.isEmpty())
    }

    private fun result(vararg windows: QuotaWindow) = DirectQuotaResult(
        planType = "plus",
        windows = windows.toList(),
        quotaState = if (windows.isEmpty()) "zero_windows" else "available",
        updatedAtMillis = 1_700_000_000_000L,
        source = QuotaSource.DIRECT,
    )

    private fun result(name: String, window: QuotaWindow) = result(window)

    private fun window(
        slot: String,
        duration: Long,
        remaining: Int?,
        resetsAt: Long?,
    ) = QuotaWindow(
        limitId = slot,
        limitName = null,
        planType = "plus",
        sourceSlot = slot,
        usedPercent = remaining?.let { 100 - it },
        remainingPercent = remaining,
        windowDurationMins = duration,
        resetsAt = resetsAt,
    )
}
