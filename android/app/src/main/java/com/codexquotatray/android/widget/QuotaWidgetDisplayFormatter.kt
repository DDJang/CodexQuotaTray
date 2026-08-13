package com.codexquotatray.android.widget

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

internal object QuotaWidgetDisplayFormatter {
    fun formatUpdatedAt(updatedAtMillis: Long): String =
        "更新于 ${SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(updatedAtMillis))}"

    fun formatResetAt(resetsAtSeconds: Long?, nowMillis: Long): String {
        if (resetsAtSeconds == null) return "重置时间未知"
        val remainingSeconds = resetsAtSeconds - nowMillis / 1_000L
        val absolute = SimpleDateFormat("M月d日 HH:mm", Locale.getDefault())
            .format(Date(resetsAtSeconds * 1_000L))
        if (remainingSeconds <= 0L) return "已到期或正在刷新 · $absolute"
        val days = remainingSeconds / 86_400L
        val hours = (remainingSeconds % 86_400L) / 3_600L
        val minutes = (remainingSeconds % 3_600L) / 60L
        val relative = when {
            days > 0L -> "$days 天 $hours 小时后重置"
            hours > 0L -> "$hours 小时 ${minutes} 分钟后重置"
            minutes > 0L -> "$minutes 分钟后重置"
            else -> "不足 1 分钟后重置"
        }
        return "$relative · $absolute"
    }
}
