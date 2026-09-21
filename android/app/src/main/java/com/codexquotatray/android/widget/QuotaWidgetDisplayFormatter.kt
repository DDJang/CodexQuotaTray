package com.codexquotatray.android.widget

import com.codexquotatray.android.UpdatedAtFormatter
import java.util.Locale
import java.util.TimeZone

internal object QuotaWidgetDisplayFormatter {
    fun formatUpdatedAt(
        updatedAtMillis: Long,
        nowMillis: Long = System.currentTimeMillis(),
        locale: Locale = Locale.getDefault(),
        timeZone: TimeZone = TimeZone.getDefault(),
    ): String = "更新于 ${UpdatedAtFormatter.formatValue(updatedAtMillis, nowMillis, locale, timeZone)}"

    fun formatResetAt(resetsAtSeconds: Long?, nowMillis: Long): String {
        if (resetsAtSeconds == null) return "重置时间未知"
        val remainingSeconds = resetsAtSeconds - nowMillis / 1_000L
        if (remainingSeconds <= 0L) return "已到期或正在刷新"
        val days = remainingSeconds / 86_400L
        val hours = (remainingSeconds % 86_400L) / 3_600L
        val minutes = (remainingSeconds % 3_600L) / 60L
        val relative = when {
            days > 0L -> "$days 天 $hours 小时后重置"
            hours > 0L -> "$hours 小时 ${minutes} 分钟后重置"
            minutes > 0L -> "$minutes 分钟后重置"
            else -> "不足 1 分钟后重置"
        }
        return relative
    }
}
