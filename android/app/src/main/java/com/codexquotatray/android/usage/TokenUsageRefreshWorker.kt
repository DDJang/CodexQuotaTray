package com.codexquotatray.android.usage

import android.content.Context
import android.content.Intent
import androidx.work.BackoffPolicy
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.refresh.AppAutomaticRefreshCoordinator
import com.codexquotatray.android.refresh.AutomaticRefreshChannel
import com.codexquotatray.android.refresh.BackgroundRefreshRetryPolicy
import com.codexquotatray.android.refresh.BackgroundRetryDecision
import com.codexquotatray.android.refresh.AndroidWorkerNetworkDiagnostics
import com.codexquotatray.android.refresh.BackgroundNetworkConstraints
import com.codexquotatray.android.refresh.BackgroundNetworkRequirement
import com.codexquotatray.android.widget.QuotaWidgetBridge
import java.util.concurrent.TimeUnit

/** Publishes a completed background sync to a visible statistics page. */
object TokenUsageRefreshEvents {
    const val ACTION_COMPLETED = "com.codexquotatray.android.TOKEN_USAGE_REFRESH_COMPLETED"

    fun notifyCompleted(context: Context) {
        QuotaWidgetBridge.syncFromCurrentMainSnapshot(context)
        context.sendBroadcast(Intent(ACTION_COMPLETED).setPackage(context.packageName))
    }
}

class TokenUsageRefreshWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : Worker(appContext, workerParams) {
    override fun doWork(): Result {
        AppLogStore.record(applicationContext, "Token 后台任务已启动")
        val settings = TokenUsageRefreshSettingsStore(applicationContext).load()
        val hasOAuth = OAuthStore(applicationContext).hasCredentials()
        val hasWindowsPairing = TokenSyncStore(applicationContext).load() != null
        AndroidWorkerNetworkDiagnostics.record(
            applicationContext,
            "Token",
            TokenUsageRefreshScheduler.networkRequirement(hasOAuth, hasWindowsPairing),
        )
        if (!settings.backgroundSyncEnabled) {
            AppLogStore.record(applicationContext, "Token 后台任务已跳过：后台同步已关闭")
            return Result.success()
        }
        if (!hasOAuth && !hasWindowsPairing) {
            AppLogStore.record(applicationContext, "Token 后台任务已跳过：缺少可用数据源", "WARN")
            return Result.success()
        }

        if (!AppAutomaticRefreshCoordinator.tryStart(
                AutomaticRefreshChannel.TOKEN,
                BackgroundRefreshRetryPolicy.reason(runAttemptCount),
                enabled = settings.backgroundSyncEnabled,
            )
        ) {
            AppLogStore.record(applicationContext, "Token 后台任务已跳过：自动刷新门控")
            return Result.success()
        }

        return try {
            runCatching { TokenUsageSyncCoordinator(applicationContext).sync() }
                .fold(
                    onSuccess = {
                        AppLogStore.record(applicationContext, "Token 后台同步完成")
                        Result.success()
                    },
                    onFailure = { error ->
                        when (tokenRetryDecision(error, runAttemptCount)) {
                            BackgroundRetryDecision.RETRY -> tokenRetryResult(
                                runAttemptCount,
                                tokenUsageSyncErrorMessage(error),
                            )
                            BackgroundRetryDecision.EXHAUSTED -> {
                                AppLogStore.record(
                                    applicationContext,
                                    "Token 后台任务本周期重试已耗尽，等待下一周期",
                                    "WARN",
                                )
                                Result.success()
                            }
                            BackgroundRetryDecision.PERMANENT -> {
                                AppLogStore.record(
                                    applicationContext,
                                    "Token 后台任务永久配对或协议失败：${tokenUsageSyncErrorMessage(error)}",
                                    "WARN",
                                )
                                Result.success()
                            }
                        }
                    },
                )
        } finally {
            AppAutomaticRefreshCoordinator.finish(AutomaticRefreshChannel.TOKEN)
        }
    }

    private fun tokenRetryResult(runAttemptCount: Int, reason: String): Result =
        when (BackgroundRefreshRetryPolicy.transientDecision(runAttemptCount)) {
            BackgroundRetryDecision.RETRY -> {
                AppLogStore.record(
                    applicationContext,
                    "Token 后台任务将重试：$reason，第 ${runAttemptCount + 1} 次补偿重试",
                    "WARN",
                )
                Result.retry()
            }
            BackgroundRetryDecision.EXHAUSTED -> {
                AppLogStore.record(applicationContext, "Token 后台任务本周期重试已耗尽，等待下一周期", "WARN")
                Result.success()
            }
            BackgroundRetryDecision.PERMANENT -> Result.success()
        }
}

object TokenUsageRefreshScheduler {
    private const val WORK_NAME = "codex_token_usage_periodic_refresh"

    internal fun shouldSchedule(
        settings: TokenUsageRefreshSettings,
        hasOAuth: Boolean,
        hasWindowsPairing: Boolean,
    ): Boolean = settings.backgroundSyncEnabled && (hasOAuth || hasWindowsPairing)

    internal fun networkRequirement(
        hasOAuth: Boolean,
        hasWindowsPairing: Boolean,
    ): BackgroundNetworkRequirement = BackgroundNetworkConstraints.quota(hasOAuth, hasWindowsPairing)

    fun cancel(context: Context) {
        WorkManager.getInstance(context.applicationContext).cancelUniqueWork(WORK_NAME)
    }

    fun schedule(context: Context) {
        val appContext = context.applicationContext
        val settings = TokenUsageRefreshSettingsStore(appContext).load()
        val hasOAuth = OAuthStore(appContext).hasCredentials()
        val hasWindowsPairing = TokenSyncStore(appContext).load() != null
        if (!shouldSchedule(settings, hasOAuth, hasWindowsPairing)) {
            cancel(appContext)
            return
        }

        val request = PeriodicWorkRequestBuilder<TokenUsageRefreshWorker>(
            settings.normalizedIntervalMinutes.toLong(),
            TimeUnit.MINUTES,
        )
            .setConstraints(
                networkRequirement(hasOAuth, hasWindowsPairing).constraints(),
            )
            .setBackoffCriteria(
                BackoffPolicy.EXPONENTIAL,
                BackgroundRefreshRetryPolicy.BACKOFF_MINUTES,
                TimeUnit.MINUTES,
            )
            .build()
        WorkManager.getInstance(appContext).enqueueUniquePeriodicWork(
            WORK_NAME,
            ExistingPeriodicWorkPolicy.UPDATE,
            request,
        )
    }
}

internal fun tokenRetryDecision(
    error: Throwable,
    runAttemptCount: Int,
): BackgroundRetryDecision = when (error) {
    is TokenUsagePairingChangedException -> BackgroundRetryDecision.PERMANENT
    is TokenUsageException -> when (error.kind) {
        TokenUsageFailureKind.PAIRING_INVALID,
        TokenUsageFailureKind.LOGIN_REQUIRED,
        TokenUsageFailureKind.INVALID_RESPONSE,
        TokenUsageFailureKind.UNSUPPORTED,
        TokenUsageFailureKind.UNAVAILABLE,
        -> BackgroundRetryDecision.PERMANENT
        TokenUsageFailureKind.OFFLINE,
        TokenUsageFailureKind.HTTP_ERROR,
        TokenUsageFailureKind.SERVER,
        -> BackgroundRefreshRetryPolicy.transientDecision(runAttemptCount)
    }
    else -> BackgroundRefreshRetryPolicy.transientDecision(runAttemptCount)
}
