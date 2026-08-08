package com.codexquotatray.android.alerts

import com.codexquotatray.android.protocol.QuotaWindow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class QuotaAlertEvaluatorTest {
    @Test
    fun thresholdNotificationsFireOnceOnDownwardCrossings() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        assertTrue(evaluator.evaluate(listOf(window(80))).isEmpty())
        assertEquals(listOf(50), evaluator.evaluate(listOf(window(49))).map { it.threshold })
        assertTrue(evaluator.evaluate(listOf(window(40))).isEmpty())
        assertEquals(listOf(20), evaluator.evaluate(listOf(window(19))).map { it.threshold })
        assertEquals(listOf(10), evaluator.evaluate(listOf(window(9))).map { it.threshold })
        assertTrue(evaluator.evaluate(listOf(window(5))).isEmpty())
    }

    @Test
    fun firstObservationAndUnknownValuesDoNotCreateFalseAlerts() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        assertTrue(evaluator.evaluate(listOf(window(null))).isEmpty())
        assertTrue(evaluator.evaluate(listOf(window(40))).isEmpty())
        assertTrue(evaluator.evaluate(listOf(window(null))).isEmpty())
        assertTrue(evaluator.evaluate(listOf(window(39))).isEmpty())
    }

    @Test
    fun resetRearmsThresholdsAndIsReportedOnce() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        evaluator.evaluate(listOf(window(40, resetAt = 1_000L)))
        assertTrue(evaluator.evaluate(listOf(window(35, resetAt = 1_100L))).isEmpty())

        val reset = evaluator.evaluate(listOf(window(95, resetAt = 10_100L)))
        assertEquals(listOf(AlertEventKind.RESET), reset.map { it.kind })
        assertTrue(evaluator.evaluate(listOf(window(95, resetAt = 10_100L))).isEmpty())
        assertEquals(
            listOf(50),
            evaluator.evaluate(listOf(window(49, resetAt = 10_100L))).map { it.threshold },
        )
    }

    private fun window(remaining: Int?, resetAt: Long? = 1_000L): QuotaWindow = QuotaWindow(
        limitId = "primary",
        limitName = null,
        sourceSlot = "primary",
        usedPercent = remaining?.let { 100 - it },
        remainingPercent = remaining,
        windowDurationMins = 300,
        resetsAt = resetAt,
    )

    private class MemoryAlertStateStore : AlertStateStore {
        private val records = mutableMapOf<String, AlertRecord>()
        private var lastRefresh: Long? = null

        override fun load(windowKey: String): AlertRecord? = records[windowKey]

        override fun save(windowKey: String, record: AlertRecord) {
            records[windowKey] = record
        }

        override fun markSuccessfulRefresh(nowMillis: Long) {
            lastRefresh = nowMillis
        }

        override fun lastSuccessfulRefresh(): Long? = lastRefresh
    }
}
