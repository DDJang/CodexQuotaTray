package com.codexquotatray.android.alerts

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import com.codexquotatray.android.AppLogStore
import com.codexquotatray.android.MainActivity
import com.codexquotatray.android.R
import com.codexquotatray.android.protocol.QuotaWindow
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.concurrent.atomic.AtomicInteger

object QuotaNotifications {
    const val CHANNEL_ID = "codex_quota_alerts"

    fun ensureChannel(context: Context) {
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

    internal fun mainActivityPendingIntent(context: Context, requestCode: Int): PendingIntent =
        PendingIntent.getActivity(
            context.applicationContext,
            requestCode,
            Intent(context, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
}

class QuotaNotificationPublisher(context: Context) {
    private val appContext = context.applicationContext
    private val settingsStore = QuotaAlertSettingsStore(appContext)

    fun publish(events: List<QuotaAlertEvent>): Boolean {
        val enabledEvents = filterEnabledAlertEvents(events, settingsStore.load())
        if (enabledEvents.isEmpty()) return true
        QuotaNotifications.ensureChannel(appContext)
        val manager = appContext.getSystemService(NotificationManager::class.java)
        if (!manager.areNotificationsEnabled()) return false
        return try {
            val expiryEvents = enabledEvents.filter { it.kind == AlertEventKind.RESET_CREDIT_EXPIRY }
            enabledEvents.filterNot { it.kind == AlertEventKind.RESET_CREDIT_EXPIRY }.forEach { event ->
                post(manager, title(event), message(event))
                AppLogStore.record(
                    appContext,
                    when (event.kind) {
                        AlertEventKind.RESET -> "额度重置通知已发送"
                        AlertEventKind.THRESHOLD -> "低额度通知已发送：${event.threshold}%阈值"
                        AlertEventKind.RESET_CREDIT_EXPIRY -> "重置卡临期通知已发送"
                    },
                )
            }
            if (expiryEvents.isNotEmpty()) {
                post(
                    manager,
                appContext.getString(R.string.reset_credit_expiry_notification_title),
                    resetCreditMessage(expiryEvents),
                )
                AppLogStore.record(appContext, "重置卡临期通知已发送")
            }
            true
        } catch (error: Exception) {
            AppLogStore.record(appContext, "额度通知发送失败：${error.javaClass.simpleName}", "WARN")
            false
        }
    }

    private fun title(event: QuotaAlertEvent): String = when (event.kind) {
        AlertEventKind.RESET -> "Codex 额度已恢复"
        AlertEventKind.THRESHOLD -> when (event.threshold) {
            10 -> "Codex 额度即将用完"
            else -> "Codex 额度较低"
        }
        AlertEventKind.RESET_CREDIT_EXPIRY ->
            appContext.getString(R.string.reset_credit_expiry_notification_title)
    }

    private fun message(event: QuotaAlertEvent): String {
        val window = displayName(event.window)
        val remaining = event.window.remainingPercent?.let { "剩余 $it%" } ?: "剩余未知"
        return when (event.kind) {
            AlertEventKind.RESET -> "$window 已重置，$remaining"
            AlertEventKind.THRESHOLD -> "$window $remaining，已低于 ${event.threshold}%"
            AlertEventKind.RESET_CREDIT_EXPIRY -> resetCreditMessage(listOf(event))
        }
    }

    private fun resetCreditMessage(events: List<QuotaAlertEvent>): String {
        val earliest = events.mapNotNull { it.resetCredit?.expiresAt }
            .minOrNull()
            ?.let { seconds ->
                DateTimeFormatter.ofPattern("MM-dd HH:mm", Locale.getDefault())
                    .withZone(ZoneId.systemDefault())
                    .format(Instant.ofEpochSecond(seconds))
            }
            ?: "未知时间"
        return if (events.size == 1) {
            appContext.getString(R.string.reset_credit_expiry_notification_single, earliest)
        } else {
            appContext.getString(
                R.string.reset_credit_expiry_notification_multiple,
                events.size,
                earliest,
            )
        }
    }

    private fun post(manager: NotificationManager, title: String, message: String) {
        val notificationId = NOTIFICATION_IDS.getAndIncrement()
        val notification = Notification.Builder(appContext, QuotaNotifications.CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle(title)
            .setContentText(message)
            .setStyle(Notification.BigTextStyle().bigText(message))
            .setContentIntent(
                QuotaNotifications.mainActivityPendingIntent(appContext, notificationId),
            )
            .setAutoCancel(true)
            .build()
        manager.notify(notificationId, notification)
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
