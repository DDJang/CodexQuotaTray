package com.codexquotatray.android.ui

import com.codexquotatray.android.protocol.LoginProbeResult
import com.codexquotatray.android.protocol.ProtocolProbeResult
import com.codexquotatray.android.protocol.QuotaWindow

enum class QuotaUiStatus {
    LOADING,
    UNAUTHENTICATED,
    LOADED,
    ERROR,
}

data class QuotaCardModel(
    val title: String,
    val remainingPercent: Int,
    val usedPercent: Int,
    val windowDurationMins: Long?,
    val resetsAt: Long?,
)

data class QuotaUiModel(
    val status: QuotaUiStatus,
    val accountLabel: String = "Codex",
    val windows: List<QuotaCardModel> = emptyList(),
    val updatedAtMillis: Long? = null,
    val message: String? = null,
)

fun ProtocolProbeResult.toQuotaUiModel(nowMillis: Long = System.currentTimeMillis()): QuotaUiModel {
    if (authenticated == false) {
        return QuotaUiModel(
            status = QuotaUiStatus.UNAUTHENTICATED,
            message = "尚未登录 Codex",
        )
    }
    if (authenticated != true) {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = "无法确认 Codex 登录状态",
        )
    }
    if (!rateLimitsReadSucceeded || rateLimitsResult != "succeeded") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = rateLimitsError(rateLimitsResult),
        )
    }
    if (quotaState == "unavailable") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = "额度窗口数据不可用",
        )
    }
    return loadedQuota(quotaWindows, quotaState, nowMillis)
}

fun LoginProbeResult.toQuotaUiModel(nowMillis: Long = System.currentTimeMillis()): QuotaUiModel {
    if (authenticated == false) {
        return QuotaUiModel(
            status = QuotaUiStatus.UNAUTHENTICATED,
            message = "尚未登录 Codex",
        )
    }
    if (authenticated != true) {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = "无法确认 Codex 登录状态",
        )
    }
    if (!rateLimitsReadSucceeded || rateLimitsResult != "succeeded") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = rateLimitsError(rateLimitsResult),
        )
    }
    if (quotaState == "unavailable") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = "额度窗口数据不可用",
        )
    }
    return loadedQuota(quotaWindows, quotaState, nowMillis)
}

private fun loadedQuota(
    windows: List<QuotaWindow>,
    quotaState: String,
    nowMillis: Long,
): QuotaUiModel {
    val cards = windows.mapIndexed { index, window ->
        QuotaCardModel(
            title = window.limitName?.takeIf(String::isNotBlank) ?: "额度窗口 ${index + 1}",
            remainingPercent = window.remainingPercent,
            usedPercent = window.usedPercent,
            windowDurationMins = window.windowDurationMins,
            resetsAt = window.resetsAt,
        )
    }
    val planType = windows.firstNotNullOfOrNull { it.planType?.takeIf(String::isNotBlank) }
    return QuotaUiModel(
        status = QuotaUiStatus.LOADED,
        accountLabel = planType?.replaceFirstChar { it.uppercase() } ?: "Codex",
        windows = cards,
        updatedAtMillis = nowMillis,
        message = if (quotaState == "zero_windows") "当前没有可用额度窗口" else null,
    )
}

private fun rateLimitsError(result: String): String = when (result) {
    "unauthenticated" -> "尚未登录 Codex"
    "unsupported" -> "当前 App Server 不支持额度读取"
    "rpc_error" -> "额度读取失败（App Server RPC 错误）"
    else -> "额度读取失败"
}
