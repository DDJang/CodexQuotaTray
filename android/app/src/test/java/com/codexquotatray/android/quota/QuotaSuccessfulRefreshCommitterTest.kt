package com.codexquotatray.android.quota

import com.codexquotatray.android.alerts.AlertEventKind
import com.codexquotatray.android.alerts.QuotaAlertEvent
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaSuccessfulRefreshCommitterTest {
    @Test
    fun directAndWindowsResultsUseTheSameSingleSuccessCommitSequence() {
        val calls = mutableListOf<String>()
        val committer = QuotaSuccessfulRefreshCommitter(
            saveSnapshot = { _, completedAt -> calls += "snapshot:$completedAt" },
            evaluateAlerts = { calls += "evaluate"; emptyList() },
            markSuccessfulRefresh = { completedAt -> calls += "mark:$completedAt" },
            publishNotifications = { calls += "notify" },
            nowMillis = { 456L },
        )

        assertTrue(committer.commit(quota(QuotaSource.DIRECT, updatedAtMillis = 123L)))
        assertEquals(listOf("snapshot:456", "evaluate", "mark:456", "notify"), calls)

        calls.clear()
        assertTrue(committer.commit(quota(QuotaSource.WINDOWS, updatedAtMillis = 999L)))
        assertEquals(listOf("snapshot:456", "evaluate", "mark:456", "notify"), calls)
    }

    @Test
    fun windowsFallbackPublishesItsQuotaAlertsThroughTheSuccessCommitter() {
        val published = mutableListOf<QuotaAlertEvent>()
        val alert = QuotaAlertEvent(
            kind = AlertEventKind.THRESHOLD,
            window = quota(QuotaSource.WINDOWS, 123L).windows.single(),
            threshold = 50,
        )
        val committer = QuotaSuccessfulRefreshCommitter(
            saveSnapshot = { _, _ -> },
            evaluateAlerts = { listOf(alert) },
            markSuccessfulRefresh = { },
            publishNotifications = { published += it },
        )

        assertTrue(committer.commit(quota(QuotaSource.WINDOWS, updatedAtMillis = 123L)))
        assertEquals(listOf(alert), published)
    }

    private fun quota(source: QuotaSource, updatedAtMillis: Long) = DirectQuotaResult(
        planType = "plus",
        windows = listOf(QuotaWindow("primary", "Primary", "plus", "primary", 10, 90, 300, 1_000)),
        quotaState = "available",
        updatedAtMillis = updatedAtMillis,
        source = source,
    )
}
