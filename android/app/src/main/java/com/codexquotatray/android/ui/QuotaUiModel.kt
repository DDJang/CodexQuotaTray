package com.codexquotatray.android.ui

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import kotlin.math.abs

enum class QuotaUiStatus {
    LOADING,
    UNAUTHENTICATED,
    LOADED,
    ERROR,
}

data class QuotaCardModel(
    val title: String,
    val remainingPercent: Int?,
    val usedPercent: Int?,
    val windowDurationMins: Long?,
    val resetsAt: Long?,
)

data class QuotaUiModel(
    val status: QuotaUiStatus,
    val accountLabel: String = "Codex",
    val windows: List<QuotaCardModel> = emptyList(),
    val updatedAtMillis: Long? = null,
    val source: QuotaSource = QuotaSource.DIRECT,
    val message: String? = null,
)

fun DirectQuotaResult.toQuotaUiModel(): QuotaUiModel {
    if (quotaState == "unavailable") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = "额度详情暂不可用",
        )
    }
    return loadedQuota(windows, planType, quotaState, updatedAtMillis, source)
}

fun unauthenticatedQuotaUiModel(): QuotaUiModel = QuotaUiModel(
    status = QuotaUiStatus.UNAUTHENTICATED,
    message = "尚未连接额度来源",
)

fun quotaLoadingUiModel(
    previous: QuotaUiModel? = null,
    message: String = "正在刷新…",
): QuotaUiModel = QuotaUiModel(
    status = QuotaUiStatus.LOADING,
    accountLabel = previous?.accountLabel ?: "Codex",
    windows = previous?.windows ?: emptyList(),
    updatedAtMillis = previous?.updatedAtMillis,
    source = previous?.source ?: QuotaSource.DIRECT,
    message = message,
)

fun quotaErrorUiModel(
    message: String,
    previous: QuotaUiModel? = null,
): QuotaUiModel = QuotaUiModel(
    status = QuotaUiStatus.ERROR,
    accountLabel = previous?.accountLabel ?: "Codex",
    windows = previous?.windows ?: emptyList(),
    updatedAtMillis = previous?.updatedAtMillis,
    source = previous?.source ?: QuotaSource.DIRECT,
    message = message,
)

private fun loadedQuota(
    windows: List<QuotaWindow>,
    planType: String?,
    quotaState: String,
    updatedAtMillis: Long,
    source: QuotaSource,
): QuotaUiModel {
    val cards = windows.mapIndexed { index, window ->
        QuotaCardModel(
            title = displayName(window, index),
            remainingPercent = window.remainingPercent,
            usedPercent = window.usedPercent,
            windowDurationMins = window.windowDurationMins,
            resetsAt = window.resetsAt,
        )
    }
    val resolvedPlanType = planType
        ?.takeIf(String::isNotBlank)
        ?: windows.firstNotNullOfOrNull { it.planType?.takeIf(String::isNotBlank) }
    return QuotaUiModel(
        status = QuotaUiStatus.LOADED,
        accountLabel = resolvedPlanType?.replaceFirstChar { it.uppercase() } ?: "Codex",
        windows = cards,
        updatedAtMillis = updatedAtMillis,
        source = source,
        message = if (quotaState == "zero_windows") "当前没有可用额度窗口" else null,
    )
}

private fun displayName(window: QuotaWindow, index: Int): String {
    val duration = window.windowDurationMins
    val durationName = when {
        duration.isNear(300L, 15L) -> "5 小时额度"
        duration.isNear(10_080L, 120L) -> "7 天额度"
        duration != null && duration > 0L && duration % 1_440L == 0L ->
            "${duration / 1_440L} 天额度"
        duration != null && duration > 0L && duration % 60L == 0L ->
            "${duration / 60L} 小时额度"
        duration != null && duration > 0L -> "${duration} 分钟额度"
        else -> null
    }
    if (durationName != null) return durationName

    val serverName = window.limitName
        ?.trim()
        ?.takeIf { it.isNotEmpty() && !it.equals("Codex", ignoreCase = true) }
    return serverName ?: if (index == 0) "额度" else "额度窗口 ${index + 1}"
}

private fun Long?.isNear(target: Long, tolerance: Long): Boolean =
    this != null && abs(this - target) <= tolerance
