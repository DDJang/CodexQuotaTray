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
    fun crossingSeveralThresholdsEmitsOnlyTheMostSevereAndConsumesAllFlags() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        evaluator.evaluate(listOf(window(80)))
        val events = evaluator.evaluate(listOf(window(8)))

        assertEquals(listOf(10), events.map { it.threshold })
        val record = store.records[QuotaAlertStateStore.stableKey(window(8))]
        assertTrue(record?.notified50 == true)
        assertTrue(record?.notified20 == true)
        assertTrue(record?.notified10 == true)
    }

    @Test
    fun crossingFromFortyFiveToFifteenEmitsOnlyTwenty() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        evaluator.evaluate(listOf(window(45)))
        val events = evaluator.evaluate(listOf(window(15)))

        assertEquals(listOf(20), events.map { it.threshold })
        val record = store.records[QuotaAlertStateStore.stableKey(window(15))]
        assertTrue(record?.notified50 == true)
        assertTrue(record?.notified20 == true)
        assertTrue(record?.notified10 == false)
    }

    @Test
    fun resetRearmsThresholdsAndIsReportedOnce() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        evaluator.evaluate(listOf(window(40, resetAt = 1_000L)))
        assertTrue(evaluator.evaluate(listOf(window(35, resetAt = 1_100L))).isEmpty())

        val reset = evaluator.evaluate(listOf(window(95, resetAt = 20_000L)))
        assertEquals(listOf(AlertEventKind.RESET), reset.map { it.kind })
        assertTrue(evaluator.evaluate(listOf(window(95, resetAt = 20_000L))).isEmpty())
        assertEquals(
            listOf(50),
            evaluator.evaluate(listOf(window(49, resetAt = 20_000L))).map { it.threshold },
        )
    }

    @Test
    fun resetUsesAdvancingResetTimeEvenWhenRemainingFalls() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        evaluator.evaluate(listOf(window(40, resetAt = 1_000L)))
        val reset = evaluator.evaluate(listOf(window(30, resetAt = 20_000L)))

        assertEquals(listOf(AlertEventKind.RESET), reset.map { it.kind })
    }

    @Test
    fun resetTimeUsesHalfWindowThresholdForMatchingFiveHourDurations() {
        val atHalfWindow = QuotaAlertEvaluator(MemoryAlertStateStore())
        atHalfWindow.evaluate(listOf(window(40, resetAt = 1_000L)))
        assertEquals(
            listOf(AlertEventKind.RESET),
            atHalfWindow.evaluate(listOf(window(40, resetAt = 10_000L))).map { it.kind },
        )

        val belowHalfWindow = QuotaAlertEvaluator(MemoryAlertStateStore())
        belowHalfWindow.evaluate(listOf(window(40, resetAt = 1_000L)))
        assertTrue(belowHalfWindow.evaluate(listOf(window(40, resetAt = 8_200L))).isEmpty())
    }

    @Test
    fun unreliableResetTimeDoesNotResetWithoutStrongRecovery() {
        val differentDuration = QuotaAlertEvaluator(MemoryAlertStateStore())
        differentDuration.evaluate(listOf(window(40, resetAt = 1_000L, durationMins = 300L)))
        assertTrue(
            differentDuration.evaluate(
                listOf(window(40, resetAt = 20_000L, durationMins = 600L)),
            ).isEmpty(),
        )

        val missingDuration = QuotaAlertEvaluator(MemoryAlertStateStore())
        missingDuration.evaluate(listOf(window(40, resetAt = 1_000L, durationMins = 300L)))
        assertTrue(
            missingDuration.evaluate(
                listOf(window(40, resetAt = 20_000L, durationMins = null)),
            ).isEmpty(),
        )
    }

    @Test
    fun strongRecoveryStillResetsWhenResetTimeIsUnreliable() {
        val evaluator = QuotaAlertEvaluator(MemoryAlertStateStore())
        evaluator.evaluate(listOf(window(20, resetAt = 1_000L, durationMins = 300L)))

        assertEquals(
            listOf(AlertEventKind.RESET),
            evaluator.evaluate(
                listOf(window(90, resetAt = 1_100L, durationMins = 600L)),
            ).map { it.kind },
        )
    }

    @Test
    fun strongRecoveryResetsWhenResetTimeIsUnchangedOrMissing() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)

        evaluator.evaluate(listOf(window(8, resetAt = 1_000L)))
        assertEquals(
            listOf(AlertEventKind.RESET),
            evaluator.evaluate(listOf(window(100, resetAt = 1_000L))).map { it.kind },
        )
        assertTrue(evaluator.evaluate(listOf(window(99, resetAt = 1_000L))).isEmpty())

        val missingResetTime = MemoryAlertStateStore()
        val missingEvaluator = QuotaAlertEvaluator(missingResetTime)
        missingEvaluator.evaluate(listOf(window(20, resetAt = null)))
        assertEquals(
            listOf(AlertEventKind.RESET),
            missingEvaluator.evaluate(listOf(window(90, resetAt = null))).map { it.kind },
        )
    }

    @Test
    fun ordinaryRecoveryDoesNotResetAndResetRearmsThresholds() {
        fun events(first: Int, second: Int): List<AlertEventKind> {
            val evaluator = QuotaAlertEvaluator(MemoryAlertStateStore())
            evaluator.evaluate(listOf(window(first)))
            return evaluator.evaluate(listOf(window(second))).map { it.kind }
        }

        assertTrue(events(40, 60).isEmpty())
        assertTrue(events(70, 100).isEmpty())
        assertTrue(events(85, 100).isEmpty())

        val evaluator = QuotaAlertEvaluator(MemoryAlertStateStore())
        evaluator.evaluate(listOf(window(40)))
        assertEquals(listOf(AlertEventKind.RESET), evaluator.evaluate(listOf(window(90))).map { it.kind })
        assertEquals(listOf(50), evaluator.evaluate(listOf(window(49))).map { it.threshold })
    }

    private fun window(
        remaining: Int?,
        resetAt: Long? = 1_000L,
        durationMins: Long? = 300L,
    ): QuotaWindow = QuotaWindow(
        limitId = "primary",
        limitName = null,
        sourceSlot = "primary",
        usedPercent = remaining?.let { 100 - it },
        remainingPercent = remaining,
        windowDurationMins = durationMins,
        resetsAt = resetAt,
    )

    private class MemoryAlertStateStore : AlertStateStore {
        val records = mutableMapOf<String, AlertRecord>()
        private var lastRefresh: Long? = null

        override fun load(windowKey: String): AlertRecord? = records[windowKey]

        override fun save(windowKey: String, record: AlertRecord) {
            records[windowKey] = record
        }

        override fun clearWindow(windowKey: String) {
            records.remove(windowKey)
        }

        override fun markSuccessfulRefresh(nowMillis: Long) {
            lastRefresh = nowMillis
        }

        override fun lastSuccessfulRefresh(): Long? = lastRefresh
    }
}
