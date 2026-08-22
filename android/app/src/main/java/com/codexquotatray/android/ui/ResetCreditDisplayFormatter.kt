package com.codexquotatray.android.ui

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

internal fun formatResetCreditExpiry(
    epochSeconds: Long?,
    zoneId: ZoneId,
    locale: Locale,
): String {
    if (epochSeconds == null) return "未知"
    return runCatching {
        DateTimeFormatter.ofPattern("MM-dd HH:mm", locale)
            .withZone(zoneId)
            .format(Instant.ofEpochSecond(epochSeconds))
    }.getOrDefault("未知")
}

internal fun formatResetCreditRemaining(
    expiresAtEpochSeconds: Long?,
    nowEpochSeconds: Long,
): String {
    val remainingSeconds = expiresAtEpochSeconds?.minus(nowEpochSeconds) ?: return "未知"
    if (remainingSeconds <= 0L) return "已到期"
    val days = remainingSeconds / 86_400L
    val hours = (remainingSeconds % 86_400L) / 3_600L
    val minutes = (remainingSeconds % 3_600L) / 60L
    return when {
        days > 0L -> "$days 天 $hours 小时"
        hours > 0L -> "$hours 小时 $minutes 分钟"
        minutes > 0L -> "$minutes 分钟"
        else -> "不足 1 分钟"
    }
}
