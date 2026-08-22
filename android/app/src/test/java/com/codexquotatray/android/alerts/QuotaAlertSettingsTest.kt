package com.codexquotatray.android.alerts

import com.codexquotatray.android.protocol.QuotaWindow
import org.junit.Assert.assertEquals
import org.junit.Test

class QuotaAlertSettingsTest {
    @Test
    fun resetCreditExpirySettingsDefaultOffAndLeadValuesAreWhitelisted() {
        assertEquals(false, QuotaAlertSettings().resetCreditExpiryEnabled)
        assertEquals(24, QuotaAlertSettings().resetCreditExpiryLeadHours)
        assertEquals(24, QuotaAlertSettingsStore.normalizeLeadHours(99))
        assertEquals(6, QuotaAlertSettingsStore.normalizeLeadHours(6))
        assertEquals(1, QuotaAlertSettingsStore.normalizeLeadHours(1))
    }

    @Test
    fun lowQuotaAndResetNotificationsCanBeDisabledIndependently() {
        val threshold = QuotaAlertEvent(
            kind = AlertEventKind.THRESHOLD,
            window = window(),
            threshold = 20,
        )
        val reset = QuotaAlertEvent(
            kind = AlertEventKind.RESET,
            window = window(),
        )
        val events = listOf(threshold, reset)

        assertEquals(
            listOf(threshold),
            filterEnabledAlertEvents(
                events,
                QuotaAlertSettings(lowQuotaEnabled = true, resetEnabled = false),
            ),
        )
        assertEquals(
            listOf(reset),
            filterEnabledAlertEvents(
                events,
                QuotaAlertSettings(lowQuotaEnabled = false, resetEnabled = true),
            ),
        )
        assertEquals(
            emptyList<QuotaAlertEvent>(),
            filterEnabledAlertEvents(
                events,
                QuotaAlertSettings(lowQuotaEnabled = false, resetEnabled = false),
            ),
        )
    }

    private fun window(): QuotaWindow = QuotaWindow(
        limitId = "primary",
        limitName = null,
        sourceSlot = "primary",
        usedPercent = 80,
        remainingPercent = 20,
        windowDurationMins = 300,
        resetsAt = 1_000L,
    )
}
