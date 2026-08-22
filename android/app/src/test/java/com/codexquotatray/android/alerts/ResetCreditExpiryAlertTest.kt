package com.codexquotatray.android.alerts

import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.ResetCreditSnapshot
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class ResetCreditExpiryAlertTest {
    private val nowMillis = 1_700_000_000_000L
    private val baseCredit = ResetCredit(
        resetType = "weekly",
        status = " available ",
        grantedAt = 1_699_000_000L,
        expiresAt = nowMillis / 1_000L + 24L * 3_600L,
        title = "Weekly reset",
    )

    @Test
    fun disabledWindowAndCountOnlyDoNotAlert() {
        val evaluator = QuotaAlertEvaluator(MemoryAlertStateStore())
        assertTrue(
            evaluator.evaluateResetCredits(
                listOf(baseCredit),
                QuotaAlertSettings(resetCreditExpiryEnabled = false),
                nowMillis,
            ).isEmpty(),
        )
        assertTrue(
            evaluator.evaluateResetCredits(
                null,
                QuotaAlertSettings(resetCreditExpiryEnabled = true),
                nowMillis,
            ).isEmpty(),
        )
        assertTrue(
            evaluator.evaluateResetCredits(
                ResetCreditSnapshot(availableCount = 2, credits = null).credits,
                QuotaAlertSettings(resetCreditExpiryEnabled = true),
                nowMillis,
            ).isEmpty(),
        )
    }

    @Test
    fun allThreeLeadChoicesRespectTheConfiguredWindow() {
        listOf(24, 6, 1).forEach { leadHours ->
            val credit = baseCredit.copy(
                expiresAt = nowMillis / 1_000L + leadHours * 3_600L,
                title = "lead-$leadHours",
            )
            val evaluator = QuotaAlertEvaluator(MemoryAlertStateStore())
            val events = evaluator.evaluateResetCredits(
                listOf(credit),
                QuotaAlertSettings(
                    resetCreditExpiryEnabled = true,
                    resetCreditExpiryLeadHours = leadHours,
                ),
                nowMillis,
            )
            assertEquals(listOf(AlertEventKind.RESET_CREDIT_EXPIRY), events.map { it.kind })
        }
    }

    @Test
    fun outsideWindowIsSilentAndEnablingInsideWindowAlertsImmediately() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)
        val outside = baseCredit.copy(expiresAt = nowMillis / 1_000L + 48L * 3_600L)
        assertTrue(
            evaluator.evaluateResetCredits(
                listOf(outside),
                QuotaAlertSettings(resetCreditExpiryEnabled = true),
                nowMillis,
            ).isEmpty(),
        )

        val inside = outside.copy(expiresAt = nowMillis / 1_000L + 6L * 3_600L)
        val enabled = evaluator.evaluateResetCredits(
            listOf(inside),
            QuotaAlertSettings(resetCreditExpiryEnabled = true, resetCreditExpiryLeadHours = 24),
            nowMillis,
        )
        assertEquals(listOf(AlertEventKind.RESET_CREDIT_EXPIRY), enabled.map { it.kind })
    }

    @Test
    fun onlyAvailableFutureExpiryCardsParticipateAndPartialCanAlert() {
        val evaluator = QuotaAlertEvaluator(MemoryAlertStateStore())
        val valid = baseCredit.copy(expiresAt = nowMillis / 1_000L + 1L * 3_600L)
        val credits = listOf(
            valid,
            valid.copy(status = "redeemed", title = "redeemed"),
            valid.copy(status = "redeeming", title = "redeeming"),
            valid.copy(status = "unknown", title = "unknown"),
            valid.copy(expiresAt = nowMillis / 1_000L - 1L, title = "expired"),
            valid.copy(expiresAt = null, title = "no-expiry"),
        )
        val events = evaluator.evaluateResetCredits(
            credits,
            QuotaAlertSettings(resetCreditExpiryEnabled = true, resetCreditExpiryLeadHours = 1),
            nowMillis,
        )
        assertEquals(1, events.size)
        assertEquals(valid, events.single().resetCredit)
    }

    @Test
    fun oneCardIsAtMostOnceAcrossEvaluatorRestartAndDirectLanSwitch() {
        val store = MemoryAlertStateStore()
        val settings = QuotaAlertSettings(resetCreditExpiryEnabled = true)
        val first = QuotaAlertEvaluator(store)
        assertEquals(1, first.evaluateResetCredits(listOf(baseCredit), settings, nowMillis).size)
        assertTrue(QuotaAlertEvaluator(store).evaluateResetCredits(listOf(baseCredit), settings, nowMillis).isEmpty())
        assertEquals(
            ResetCreditFingerprint.create(baseCredit),
            ResetCreditFingerprint.create(baseCredit.copy(status = "AVAILABLE")),
        )
    }

    @Test
    fun multipleCardsAggregateAsMultipleExpiryEventsAndFailureCanRetry() {
        val store = MemoryAlertStateStore()
        val evaluator = QuotaAlertEvaluator(store)
        val firstCredit = baseCredit.copy(expiresAt = nowMillis / 1_000L + 3_600L)
        val firstExpiry = firstCredit.expiresAt ?: error("fixture expiry missing")
        val secondCredit = firstCredit.copy(title = "Second", expiresAt = firstExpiry + 60)
        val events = evaluator.evaluateResetCredits(
            listOf(firstCredit, secondCredit),
            QuotaAlertSettings(resetCreditExpiryEnabled = true),
            nowMillis,
        )
        assertEquals(2, events.size)
        evaluator.restoreLastEvaluation()
        assertEquals(2, evaluator.evaluateResetCredits(
            listOf(firstCredit, secondCredit),
            QuotaAlertSettings(resetCreditExpiryEnabled = true),
            nowMillis,
        ).size)
    }

    @Test
    fun schedulerPlannerUsesEarliestUnnotifiedLocalCardWithoutNetwork() {
        val store = MemoryAlertStateStore()
        val baseExpiry = baseCredit.expiresAt ?: error("fixture expiry missing")
        val second = baseCredit.copy(title = "Second", expiresAt = baseExpiry + 60)
        val secondExpiry = second.expiresAt ?: error("fixture expiry missing")
        val snapshot = ResetCreditSnapshot(availableCount = 2, credits = listOf(baseCredit, second))
        val due = com.codexquotatray.android.quota.ResetCreditExpiryReminderScheduler.nextReminderAt(
            snapshot,
            leadHours = 24,
            nowMillis = nowMillis,
            stateStore = store,
        )
        assertEquals(baseExpiry * 1_000L - 24L * 3_600_000L, due)
        store.saveResetCredit(
            ResetCreditFingerprint.create(baseCredit),
            ResetCreditAlertRecord(notified = true, lastSeenMillis = nowMillis, expiresAtMillis = baseExpiry * 1_000L),
        )
        assertEquals(
            secondExpiry * 1_000L - 24L * 3_600_000L,
            com.codexquotatray.android.quota.ResetCreditExpiryReminderScheduler.nextReminderAt(
                snapshot,
                24,
                nowMillis,
                store,
            ),
        )
    }

    @Test
    fun evaluationOfFirstDueCardLeavesNextDueCardForTheNextSchedule() {
        val store = MemoryAlertStateStore()
        val firstCredit = baseCredit.copy(
            expiresAt = nowMillis / 1_000L + 3_600L,
            title = "First",
        )
        val firstExpiry = firstCredit.expiresAt ?: error("fixture expiry missing")
        val secondCredit = firstCredit.copy(
            expiresAt = firstExpiry + 24L * 3_600L,
            title = "Second",
        )
        val secondExpiry = secondCredit.expiresAt ?: error("fixture expiry missing")
        val snapshot = ResetCreditSnapshot(
            availableCount = 2,
            credits = listOf(firstCredit, secondCredit),
        )

        val events = QuotaAlertEvaluator(store).evaluateResetCredits(
            snapshot.credits,
            QuotaAlertSettings(resetCreditExpiryEnabled = true),
            nowMillis,
            snapshot.availableCount,
        )

        assertEquals(listOf(firstCredit), events.map { it.resetCredit })
        assertEquals(
            secondExpiry * 1_000L - 24L * 3_600_000L,
            com.codexquotatray.android.quota.ResetCreditExpiryReminderScheduler.nextReminderAt(
                snapshot,
                leadHours = 24,
                nowMillis = nowMillis,
                stateStore = store,
            ),
        )
    }

    private class MemoryAlertStateStore : AlertStateStore {
        private val windows = mutableMapOf<String, AlertRecord>()
        private val credits = mutableMapOf<String, ResetCreditAlertRecord>()

        override fun load(windowKey: String): AlertRecord? = windows[windowKey]
        override fun save(windowKey: String, record: AlertRecord) { windows[windowKey] = record }
        override fun clearWindow(windowKey: String) { windows.remove(windowKey) }
        override fun markSuccessfulRefresh(nowMillis: Long) = Unit
        override fun lastSuccessfulRefresh(): Long? = null
        override fun loadResetCredit(fingerprint: String): ResetCreditAlertRecord? = credits[fingerprint]
        override fun saveResetCredit(fingerprint: String, record: ResetCreditAlertRecord) { credits[fingerprint] = record }
        override fun clearResetCredit(fingerprint: String) { credits.remove(fingerprint) }
        override fun resetCreditKeys(): Set<String> = credits.keys
    }
}
