package com.codexquotatray.android.quota

import android.content.Context
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.refresh.AppAutomaticRefreshCoordinator
import com.codexquotatray.android.refresh.AutomaticRefreshChannel
import androidx.work.BackoffPolicy
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.refresh.BackgroundRefreshRetryPolicy
import com.codexquotatray.android.refresh.BackgroundRetryDecision
import com.codexquotatray.android.refresh.AndroidWorkerNetworkDiagnostics
import com.codexquotatray.android.refresh.BackgroundNetworkConstraints
import com.codexquotatray.android.refresh.BackgroundNetworkRequirement

class QuotaRefreshWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : Worker(appContext, workerParams) {
    override fun doWork(): Result {
        AppLogStore.record(applicationContext, "额度后台任务已启动")
        val settings = QuotaRefreshSettingsStore(applicationContext).load()
        if (!settings.enabled) {
            AppLogStore.record(applicationContext, "额度后台任务已跳过：设置已关闭")
            return Result.success()
        }
        val hasOAuth = OAuthStore(applicationContext).load() != null
        val hasWindowsPairing = TokenSyncStore(applicationContext).load() != null
        AndroidWorkerNetworkDiagnostics.record(
            applicationContext,
            "Quota",
            QuotaRefreshScheduler.networkRequirement(hasWindowsPairing),
        )
        if (!hasOAuth && !hasWindowsPairing) {
            AppLogStore.record(applicationContext, "额度后台任务已跳过：缺少可用数据源", "WARN")
            return Result.success()
        }

        if (!AppAutomaticRefreshCoordinator.tryStart(
                AutomaticRefreshChannel.QUOTA,
                BackgroundRefreshRetryPolicy.reason(runAttemptCount),
                enabled = settings.enabled,
            )
        ) {
            AppLogStore.record(applicationContext, "额度后台任务已跳过：自动刷新门控")
            return Result.success()
        }

        return try {
            val result = CodexQuotaRepository(applicationContext).refresh()
            if (result.quotaState != "unavailable") {
                QuotaRefreshEvents.notifyCompleted(applicationContext)
            }
            AppLogStore.record(
                applicationContext,
                if (result.source == QuotaSource.WINDOWS) "额度后台任务 Windows fallback 成功" else "额度后台任务 OpenAI direct 成功",
            )
            Result.success()
        } catch (error: QuotaReadException) {
            when (quotaRetryDecision(error.kind, runAttemptCount)) {
                BackgroundRetryDecision.RETRY -> {
                    AppLogStore.record(
                        applicationContext,
                        "额度后台任务将重试：${error.kind}，第 ${runAttemptCount + 1} 次补偿重试",
                        "WARN",
                    )
                    Result.retry()
                }
                BackgroundRetryDecision.EXHAUSTED -> {
                    AppLogStore.record(applicationContext, "额度后台任务本周期重试已耗尽，等待下一周期", "WARN")
                    Result.success()
                }
                BackgroundRetryDecision.PERMANENT -> {
                    AppLogStore.record(applicationContext, "额度后台任务永久失败：${error.kind}", "WARN")
                    Result.success()
                }
            }
        } finally {
            AppAutomaticRefreshCoordinator.finish(AutomaticRefreshChannel.QUOTA)
        }
    }
}

object QuotaRefreshScheduler {
    private const val WORK_NAME = "codex_quota_periodic_refresh"

    fun cancel(context: Context) {
        WorkManager.getInstance(context.applicationContext).cancelUniqueWork(WORK_NAME)
    }

    fun schedule(context: Context) {
        val appContext = context.applicationContext
        val settings = QuotaRefreshSettingsStore(appContext).load()
        val workManager = WorkManager.getInstance(appContext)
        val hasOAuth = OAuthStore(appContext).load() != null
        val hasWindowsPairing = TokenSyncStore(appContext).load() != null
        if (!shouldSchedule(settings, hasOAuth, hasWindowsPairing)) {
            cancel(appContext)
            return
        }

        val constraints = networkRequirement(hasWindowsPairing).constraints()
        val request = PeriodicWorkRequestBuilder<QuotaRefreshWorker>(
            settings.normalizedIntervalMinutes.toLong(),
            TimeUnit.MINUTES,
        )
            .setConstraints(constraints)
            .setBackoffCriteria(
                BackoffPolicy.EXPONENTIAL,
                BackgroundRefreshRetryPolicy.BACKOFF_MINUTES,
                TimeUnit.MINUTES,
            )
            .build()
        workManager.enqueueUniquePeriodicWork(
            WORK_NAME,
            ExistingPeriodicWorkPolicy.UPDATE,
            request,
        )
    }

    internal fun networkRequirement(hasWindowsPairing: Boolean): BackgroundNetworkRequirement =
        BackgroundNetworkConstraints.quota(hasWindowsPairing)

    internal fun shouldSchedule(
        settings: QuotaRefreshSettings,
        hasOAuth: Boolean,
        hasWindowsPairing: Boolean,
    ): Boolean = settings.enabled && (hasOAuth || hasWindowsPairing)
}

internal fun quotaRetryDecision(
    kind: QuotaReadFailureKind,
    runAttemptCount: Int,
): BackgroundRetryDecision = when (kind) {
    QuotaReadFailureKind.NETWORK,
    QuotaReadFailureKind.SERVER,
    -> BackgroundRefreshRetryPolicy.transientDecision(runAttemptCount)
    QuotaReadFailureKind.LOGIN_REQUIRED,
    QuotaReadFailureKind.INVALID_RESPONSE,
    -> BackgroundRetryDecision.PERMANENT
}
