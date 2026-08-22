package com.codexquotatray.android.quota

import android.content.Context
import androidx.work.BackoffPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import com.codexquotatray.android.alerts.QuotaAlertEvaluator
import com.codexquotatray.android.alerts.QuotaAlertSettingsStore
import com.codexquotatray.android.alerts.QuotaAlertStateStore
import com.codexquotatray.android.alerts.QuotaNotificationPublisher
import com.codexquotatray.android.alerts.ResetCreditFingerprint
import com.codexquotatray.android.alerts.AlertStateStore
import com.codexquotatray.android.protocol.ResetCreditSnapshot
import java.util.concurrent.TimeUnit

/** Schedules only local reset-credit evaluation; it never performs a network read. */
object ResetCreditExpiryReminderScheduler {
    private const val WORK_NAME = "codex_reset_credit_expiry_reminder"
    private const val RETRY_DELAY_MINUTES = 15L

    fun cancel(context: Context) {
        WorkManager.getInstance(context.applicationContext).cancelUniqueWork(WORK_NAME)
    }

    fun schedule(context: Context) {
        val appContext = context.applicationContext
        val settings = QuotaAlertSettingsStore(appContext).load()
        if (!settings.resetCreditExpiryEnabled) {
            cancel(appContext)
            return
        }

        val snapshot = QuotaSnapshotStore(appContext).load()
        val stateStore = QuotaAlertStateStore(appContext)
        val dueAt = nextReminderAt(
            snapshot?.resetCredits,
            settings.resetCreditExpiryLeadHours,
            System.currentTimeMillis(),
            stateStore,
        )
        if (dueAt == null) {
            cancel(appContext)
            return
        }

        val delayMillis = (dueAt - System.currentTimeMillis()).coerceAtLeast(0L)
        val request = OneTimeWorkRequestBuilder<ResetCreditExpiryReminderWorker>()
            .setInitialDelay(delayMillis, TimeUnit.MILLISECONDS)
            .setBackoffCriteria(
                BackoffPolicy.EXPONENTIAL,
                RETRY_DELAY_MINUTES,
                TimeUnit.MINUTES,
            )
            .build()
        WorkManager.getInstance(appContext).enqueueUniqueWork(
            WORK_NAME,
            ExistingWorkPolicy.REPLACE,
            request,
        )
    }

    /** Evaluates the latest persisted snapshot and returns whether delivery succeeded. */
    fun evaluateNow(context: Context): Boolean {
        val appContext = context.applicationContext
        val settings = QuotaAlertSettingsStore(appContext).load()
        if (!settings.resetCreditExpiryEnabled) {
            cancel(appContext)
            return true
        }
        val snapshot = QuotaSnapshotStore(appContext).load() ?: run {
            cancel(appContext)
            return true
        }
        val evaluator = QuotaAlertEvaluator(QuotaAlertStateStore(appContext))
        val events = evaluator.evaluateResetCredits(
            snapshot.resetCredits?.credits,
            settings,
            System.currentTimeMillis(),
            snapshot.resetCredits?.availableCount,
        )
        if (events.isEmpty()) return true
        val published = runCatching {
            QuotaNotificationPublisher(appContext).publish(events)
        }.getOrDefault(false)
        if (!published) evaluator.restoreLastEvaluation()
        return published
    }

    internal fun nextReminderAt(
        snapshot: ResetCreditSnapshot?,
        leadHours: Int,
        nowMillis: Long,
        stateStore: AlertStateStore,
    ): Long? {
        if (snapshot?.availableCount == 0L) return null
        val normalizedLeadHours = when (leadHours) {
            6 -> 6
            1 -> 1
            else -> 24
        }
        return snapshot?.credits.orEmpty()
            .asSequence()
            .filter { credit ->
                credit.status?.trim()?.equals("available", ignoreCase = true) == true
                    && credit.expiresAt != null
                    && credit.expiresAt > nowMillis / 1_000L
            }
            .map { credit ->
                val fingerprint = ResetCreditFingerprint.create(credit)
                val dueAt = credit.expiresAt!! * 1_000L - normalizedLeadHours * 3_600_000L
                dueAt to stateStore.loadResetCredit(fingerprint)
            }
            .filter { (_, state) -> state?.notified != true }
            .map { (dueAt, _) -> dueAt }
            .minOrNull()
    }
}

class ResetCreditExpiryReminderWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : Worker(appContext, workerParams) {
    override fun doWork(): Result {
        if (!ResetCreditExpiryReminderScheduler.evaluateNow(applicationContext)) {
            return Result.retry()
        }
        ResetCreditExpiryReminderScheduler.schedule(applicationContext)
        return Result.success()
    }
}
