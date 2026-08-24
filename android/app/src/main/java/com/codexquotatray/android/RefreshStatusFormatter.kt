package com.codexquotatray.android

import androidx.compose.foundation.layout.Row
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.unit.sp

/** Presentation-only wording shared by the quota and Token pages. */
internal object RefreshStatusFormatter {
    fun loaded(source: String, updatedAt: String?): String {
        val time = updatedAt?.takeIf { it.isNotBlank() }?.let { "更新于 $it" } ?: "尚未更新"
        return "$time · $source"
    }

    fun refreshing(hasCachedData: Boolean): String =
        if (hasCachedData) "正在刷新… · 显示上次数据" else "正在刷新…"

    fun failure(reason: String?, updatedAt: String? = null): String {
        val message = reason?.trim()?.takeIf { it.isNotEmpty() } ?: "暂不可用"
        return if (updatedAt?.isNotBlank() == true) {
            "更新于 $updatedAt · 刷新失败：$message"
        } else {
            "刷新失败：$message"
        }
    }

    fun tokenRefreshing(hasCachedData: Boolean): String =
        if (hasCachedData) "正在同步… · 显示上次数据" else "正在同步…"

    fun tokenFailure(reason: String?, updatedAt: String? = null): String {
        val message = reason?.trim()?.takeIf { it.isNotEmpty() } ?: "暂不可用"
        return if (updatedAt?.isNotBlank() == true) {
            "更新于 $updatedAt · 同步失败：$message"
        } else {
            "同步失败：$message"
        }
    }

    fun quotaNoSource(): String = "尚未连接额度来源"

    fun tokenUnpaired(): String = "尚未连接 Token 数据来源"

    fun tokenPairedWithoutData(): String = "数据来源已连接 · 暂无 Token 数据"
}

internal fun refreshStatusErrorMarker(status: String): String? =
    listOf("刷新失败：", "同步失败：").firstOrNull { status.contains(it) }

internal fun shortQuotaRefreshFailure(message: String?): String =
    if (message?.contains("无法连接") == true) "网络连接异常"
    else message?.trim()?.takeIf { it.isNotEmpty() } ?: "额度服务暂不可用"

@Composable
internal fun RefreshStatusLine(status: String) {
    val palette = LocalQuotaPalette.current
    val errorMarker = refreshStatusErrorMarker(status)
    val errorStart = errorMarker?.let { status.indexOf(it) } ?: -1
    if (errorStart >= 0) {
        val prefix = status.substring(0, errorStart).removeSuffix(" · ")
        Row {
            if (prefix.isNotEmpty()) {
                Text(prefix, fontSize = 14.sp, color = palette.color(palette.muted))
                Text(" · ", fontSize = 14.sp, color = palette.color(palette.muted))
            }
            Text(status.substring(errorStart), fontSize = 14.sp, color = palette.color(palette.error))
        }
    } else {
        Text(status, fontSize = 14.sp, color = palette.color(palette.muted))
    }
}
