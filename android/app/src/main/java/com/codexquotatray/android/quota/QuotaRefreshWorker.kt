package com.codexquotatray.android.quota

import android.content.Context
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.auth.OAuthStore
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit

class QuotaRefreshWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : Worker(appContext, workerParams) {
    override fun doWork(): Result = try {
        val result = CodexQuotaRepository(applicationContext).refresh()
        if (result.quotaState != "unavailable") {
            QuotaRefreshEvents.notifyCompleted(applicationContext)
        }
        Result.success()
    } catch (error: QuotaReadException) {
        AppLogStore.record(
            applicationContext,
            "后台刷新失败：${error.message}",
            "WARN",
        )
        // The next scheduled run will retry ordinary network or API failures.
        Result.success()
    }
}

object QuotaRefreshScheduler {
    private const val WORK_NAME = "codex_quota_periodic_refresh"

    fun schedule(context: Context) {
        val appContext = context.applicationContext
        val settings = QuotaRefreshSettingsStore(appContext).load()
        val workManager = WorkManager.getInstance(appContext)
        workManager.cancelUniqueWork(WORK_NAME)
        if (!settings.enabled || OAuthStore(appContext).load() == null) return

        val constraints = Constraints.Builder()
            .setRequiredNetworkType(NetworkType.CONNECTED)
            .build()
        val request = PeriodicWorkRequestBuilder<QuotaRefreshWorker>(
            settings.normalizedIntervalMinutes.toLong(),
            TimeUnit.MINUTES,
        )
            .setConstraints(constraints)
            .build()
        workManager.enqueueUniquePeriodicWork(
            WORK_NAME,
            ExistingPeriodicWorkPolicy.KEEP,
            request,
        )
    }
}
