package com.codexquotatray.android.usage

import android.content.Context
import android.content.Intent
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.quota.AndroidLanAvailability
import java.util.concurrent.TimeUnit

/** Publishes a completed background sync to a visible statistics page. */
object TokenUsageRefreshEvents {
    const val ACTION_COMPLETED = "com.codexquotatray.android.TOKEN_USAGE_REFRESH_COMPLETED"

    fun notifyCompleted(context: Context) {
        context.sendBroadcast(Intent(ACTION_COMPLETED).setPackage(context.packageName))
    }
}

class TokenUsageRefreshWorker(
    appContext: Context,
    workerParams: WorkerParameters,
) : Worker(appContext, workerParams) {
    override fun doWork(): Result {
        val settings = TokenUsageRefreshSettingsStore(applicationContext).load()
        val pairingStore = TokenSyncStore(applicationContext)
        val pairing = pairingStore.load()
        if (!TokenUsageRefreshScheduler.shouldSchedule(settings, pairing != null)) return Result.success()
        if (!TokenUsageRefreshScheduler.shouldRunOnWifiLan(
                settings = settings,
                hasPairing = pairing != null,
                isWifiLanAvailable = AndroidLanAvailability(applicationContext).isAvailable(),
            )
        ) {
            AppLogStore.record(applicationContext, "Token 后台同步已跳过：当前不在 Wi-Fi", "INFO")
            return Result.success()
        }

        return runCatching { TokenUsageSyncCoordinator(applicationContext).sync(pairing!!) }
            .fold(
                onSuccess = { synced ->
                    AppLogStore.record(applicationContext, "Token 后台同步完成")
                    Result.success()
                },
                onFailure = { error ->
                    val message = tokenUsageSyncErrorMessage(error)
                    AppLogStore.record(applicationContext, "Token 后台同步失败：$message", "WARN")
                    // A later periodic run may recover an offline Windows host.
                    Result.success()
                },
            )
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
            .build()
        WorkManager.getInstance(appContext).enqueueUniquePeriodicWork(
            WORK_NAME,
            ExistingPeriodicWorkPolicy.UPDATE,
            request,
        )
    }
}
