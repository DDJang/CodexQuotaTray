package com.codexquotatray.android.usage

import android.content.Context
import android.content.Intent
import androidx.work.Constraints
import androidx.work.BackoffPolicy
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.quota.AndroidLanAvailability
import com.codexquotatray.android.refresh.AppAutomaticRefreshCoordinator
import com.codexquotatray.android.refresh.AutomaticRefreshChannel
import com.codexquotatray.android.refresh.BackgroundRefreshRetryPolicy
import com.codexquotatray.android.refresh.BackgroundRetryDecision
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
        val pairingStore = TokenSyncStore(applicationContext)
        val pairing = pairingStore.load()
        if (!settings.backgroundSyncEnabled) {
            AppLogStore.record(applicationContext, "Token 后台任务已跳过：后台同步已关闭")
            return Result.success()
        }
        if (pairing == null) {
            AppLogStore.record(applicationContext, "Token 后台任务已跳过：缺少 Windows 配对", "WARN")
            return Result.success()
        }
        if (!TokenUsageRefreshScheduler.shouldRunOnWifiLan(
                settings = settings,
                hasPairing = true,
                isWifiLanAvailable = AndroidLanAvailability(applicationContext).isAvailable(),
            )
        ) {
            return tokenRetryResult(
                runAttemptCount,
                "Wi-Fi LAN 暂不可用",
            )
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
            runCatching { TokenUsageSyncCoordinator(applicationContext).sync(pairing) }
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

    internal fun shouldSchedule(settings: TokenUsageRefreshSettings, hasPairing: Boolean): Boolean =
        settings.backgroundSyncEnabled && hasPairing

    /** The Worker may start without validated Internet, but only syncs on a real Wi-Fi LAN. */
    internal fun shouldRunOnWifiLan(
        settings: TokenUsageRefreshSettings,
        hasPairing: Boolean,
        isWifiLanAvailable: Boolean,
    ): Boolean = shouldSchedule(settings, hasPairing) && isWifiLanAvailable

    /** Paired Token sync is local-only and must not wait for validated Internet. */
    internal fun requiredNetworkType(): NetworkType = NetworkType.NOT_REQUIRED

    fun cancel(context: Context) {
        WorkManager.getInstance(context.applicationContext).cancelUniqueWork(WORK_NAME)
    }

    fun schedule(context: Context) {
        val appContext = context.applicationContext
        val settings = TokenUsageRefreshSettingsStore(appContext).load()
        val hasPairing = TokenSyncStore(appContext).load() != null
        if (!shouldSchedule(settings, hasPairing)) {
            cancel(appContext)
            return
        }

        val request = PeriodicWorkRequestBuilder<TokenUsageRefreshWorker>(
            settings.normalizedIntervalMinutes.toLong(),
            TimeUnit.MINUTES,
        )
            .setConstraints(
                Constraints.Builder().setRequiredNetworkType(requiredNetworkType()).build(),
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
        TokenUsageFailureKind.INVALID_RESPONSE,
        TokenUsageFailureKind.UNSUPPORTED,
        -> BackgroundRetryDecision.PERMANENT
        TokenUsageFailureKind.OFFLINE,
        TokenUsageFailureKind.HTTP_ERROR,
        -> BackgroundRefreshRetryPolicy.transientDecision(runAttemptCount)
    }
    else -> BackgroundRefreshRetryPolicy.transientDecision(runAttemptCount)
}
