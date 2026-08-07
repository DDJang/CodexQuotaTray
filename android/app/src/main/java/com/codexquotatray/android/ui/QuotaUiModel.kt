package com.codexquotatray.android.ui

import com.codexquotatray.android.protocol.LoginProbeResult
import com.codexquotatray.android.protocol.ProtocolProbeResult
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
    if (rateLimitsReadSucceeded && rateLimitsResult == "succeeded") {
        if (quotaState == "unavailable") {
            return QuotaUiModel(
                status = QuotaUiStatus.ERROR,
                message = "额度读取失败",
            )
        }
        return loadedQuota(quotaWindows, quotaState, nowMillis)
    }
    if (authenticated == false) {
        return QuotaUiModel(
            status = QuotaUiStatus.UNAUTHENTICATED,
            message = "尚未登录 Codex",
        )
    }
    if (authenticated != true) {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = userFacingError(lastError, "无法确认 Codex 登录状态"),
        )
    }
    return QuotaUiModel(
        status = QuotaUiStatus.ERROR,
        message = rateLimitsError(rateLimitsResult, lastError),
    )
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
            message = userFacingError(lastError, "无法确认 Codex 登录状态"),
        )
    }
    if (!rateLimitsReadSucceeded || rateLimitsResult != "succeeded") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = rateLimitsError(rateLimitsResult, lastError),
        )
    }
    if (quotaState == "unavailable") {
        return QuotaUiModel(
            status = QuotaUiStatus.ERROR,
            message = "额度读取失败",
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
            title = displayName(window, index),
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

private fun rateLimitsError(result: String, lastError: String?): String = when {
    result == "unauthenticated" -> "登录状态已失效，请重新登录"
    lastError == "unauthenticated" -> "登录状态已失效，请重新登录"
    result == "unsupported" -> "当前 App Server 不支持额度读取"
    result == "rpc_error" -> "额度读取失败"
    result == "transport_error" || lastError.isTransportFailure() -> "无法连接 Codex"
    else -> "额度读取失败"
}

private fun userFacingError(lastError: String?, fallback: String): String = when {
    lastError == "unauthenticated" -> "登录状态已失效，请重新登录"
    lastError.isBackendStartupFailure() -> "Codex 后端启动失败"
    lastError.isTransportFailure() -> "无法连接 Codex"
    else -> fallback
}

private fun String?.isBackendStartupFailure(): Boolean = this?.let {
    it.startsWith("startup_failed") ||
        it.startsWith("ready_") ||
        it.startsWith("http_") ||
        it == "initialize_rpc_error" ||
        it == "initialize_protocol_error"
} == true

private fun String?.isTransportFailure(): Boolean = this in setOf(
    "connection_refused",
    "connection_timeout",
    "timeout",
    "other_ioexception",
    "websocket_open_failed",
    "websocket_closed_before_initialize",
    "cleartext_not_permitted",
    "interrupted",
)
