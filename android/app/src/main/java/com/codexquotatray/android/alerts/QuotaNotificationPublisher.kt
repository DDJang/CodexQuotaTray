package com.codexquotatray.android.alerts

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import com.codexquotatray.android.protocol.QuotaWindow
import java.util.concurrent.atomic.AtomicInteger

object QuotaNotifications {
    const val CHANNEL_ID = "codex_quota_alerts"
    private const val TEST_NOTIFICATION_ID = 9000

    fun ensureChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "Codex 额度提醒",
                NotificationManager.IMPORTANCE_DEFAULT,
            ).apply {
                description = "Codex 低额度和重置提醒"
            },
        )
    }

    fun sendTest(context: Context): Boolean {
        ensureChannel(context)
        val manager = context.getSystemService(NotificationManager::class.java) ?: return false
        if (!manager.areNotificationsEnabled()) return false
        manager.notify(
            TEST_NOTIFICATION_ID,
            Notification.Builder(context, CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setContentTitle("CodexQuota 通知测试")
                .setContentText("如果你看到这条通知，系统通知已正常开启。")
                .setAutoCancel(true)
                .build(),
        )
        return true
    }
}

class QuotaNotificationPublisher(context: Context) {
    private val appContext = context.applicationContext
    private val settingsStore = QuotaAlertSettingsStore(appContext)

    fun publish(events: List<QuotaAlertEvent>) {
        val enabledEvents = filterEnabledAlertEvents(events, settingsStore.load())
        if (enabledEvents.isEmpty()) return
        QuotaNotifications.ensureChannel(appContext)
        val manager = appContext.getSystemService(NotificationManager::class.java)
        if (!manager.areNotificationsEnabled()) return
        enabledEvents.forEach { event ->
            val notification = Notification.Builder(appContext, QuotaNotifications.CHANNEL_ID)
                .setSmallIcon(android.R.drawable.ic_dialog_info)
                .setContentTitle(title(event))
                .setContentText(message(event))
                .setStyle(Notification.BigTextStyle().bigText(message(event)))
                .setAutoCancel(true)
                .build()
            manager.notify(NOTIFICATION_IDS.getAndIncrement(), notification)
        }
    }

    private fun title(event: QuotaAlertEvent): String = when (event.kind) {
        AlertEventKind.RESET -> "Codex 额度已恢复"
        AlertEventKind.THRESHOLD -> when (event.threshold) {
            10 -> "Codex 额度即将用完"
            else -> "Codex 额度较低"
        }
    }

    private fun message(event: QuotaAlertEvent): String {
        val window = displayName(event.window)
        val remaining = event.window.remainingPercent?.let { "剩余 $it%" } ?: "剩余未知"
        return when (event.kind) {
            AlertEventKind.RESET -> "$window 已重置，$remaining"
            AlertEventKind.THRESHOLD -> "$window $remaining，已低于 ${event.threshold}%"
        }
    }

    private fun displayName(window: QuotaWindow): String {
        val duration = window.windowDurationMins
        return when {
            duration != null && kotlin.math.abs(duration - 300L) <= 15L -> "5 小时额度"
            duration != null && kotlin.math.abs(duration - 10_080L) <= 120L -> "7 天额度"
            duration != null && duration > 0L && duration % 1_440L == 0L ->
                "${duration / 1_440L} 天额度"
            duration != null && duration > 0L && duration % 60L == 0L ->
                "${duration / 60L} 小时额度"
            else -> window.limitName?.takeIf(String::isNotBlank) ?: "额度窗口"
        }
    }

    companion object {
        private val NOTIFICATION_IDS = AtomicInteger(1000)
    }
}
