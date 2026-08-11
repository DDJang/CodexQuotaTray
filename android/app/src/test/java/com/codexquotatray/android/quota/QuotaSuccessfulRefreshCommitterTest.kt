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
            publishNotifications = { calls += "notify"; true },
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
            publishNotifications = { published += it; true },
        )

        assertTrue(committer.commit(quota(QuotaSource.WINDOWS, updatedAtMillis = 123L)))
        assertEquals(listOf(alert), published)
    }

    @Test
    fun failedNotificationDoesNotConsumeFutureResetAlert() {
        val stateStore = InMemoryAlertStateStore()
        val evaluator = com.codexquotatray.android.alerts.QuotaAlertEvaluator(stateStore)
        evaluator.evaluate(listOf(window(8, resetAt = 1_000L)))

        var publishSucceeded = false
        val committer = QuotaSuccessfulRefreshCommitter(
            saveSnapshot = { _, _ -> },
            evaluateAlerts = evaluator::evaluate,
            markSuccessfulRefresh = { },
            publishNotifications = { publishSucceeded },
            restoreAlerts = evaluator::restoreLastEvaluation,
            nowMillis = { 456L },
        )

        assertTrue(committer.commit(quota(QuotaSource.WINDOWS, 456L, remaining = 100)))
        publishSucceeded = true
        assertEquals(
            listOf(AlertEventKind.RESET),
            evaluator.evaluate(listOf(window(100, resetAt = 1_000L))).map { it.kind },
        )
    }

    private fun quota(source: QuotaSource, updatedAtMillis: Long, remaining: Int = 90) = DirectQuotaResult(
        planType = "plus",
        windows = listOf(QuotaWindow("primary", "Primary", "plus", "primary", 100 - remaining, remaining, 300, 1_000)),
        quotaState = "available",
        updatedAtMillis = updatedAtMillis,
        source = source,
    )

    private fun window(remaining: Int, resetAt: Long?) =
        quota(QuotaSource.WINDOWS, 0L, remaining).windows.single().copy(resetsAt = resetAt)

    private class InMemoryAlertStateStore : com.codexquotatray.android.alerts.AlertStateStore {
        private var record: com.codexquotatray.android.alerts.AlertRecord? = null
        override fun load(windowKey: String) = record
        override fun save(windowKey: String, record: com.codexquotatray.android.alerts.AlertRecord) {
            this.record = record
        }
        override fun clearWindow(windowKey: String) { record = null }
        override fun markSuccessfulRefresh(nowMillis: Long) = Unit
        override fun lastSuccessfulRefresh(): Long? = null
    }
}
