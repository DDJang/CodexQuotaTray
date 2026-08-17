package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Test

class RefreshStatusFormatterTest {
    @Test
    fun quotaLoadedStatusNamesItsSourceAndDataTime() {
        assertEquals("更新于 15:03 · OpenAI", RefreshStatusFormatter.loaded("OpenAI", "15:03"))
        assertEquals("更新于 15:03 · Windows", RefreshStatusFormatter.loaded("Windows", "15:03"))
    }

    @Test
    fun refreshingStatusDistinguishesCachedAndEmptyStates() {
        assertEquals("正在刷新…", RefreshStatusFormatter.refreshing(hasCachedData = false))
        assertEquals("正在刷新… · 显示上次数据", RefreshStatusFormatter.refreshing(hasCachedData = true))
    }

    @Test
    fun failureStatusKeepsTheDisplayedDataTimeWhenAvailable() {
        assertEquals("刷新失败：网络连接异常", RefreshStatusFormatter.failure("网络连接异常"))
        assertEquals(
            "更新于 15:03 · 刷新失败：Windows 暂不可用",
            RefreshStatusFormatter.failure("Windows 暂不可用", "15:03"),
        )
    }

    @Test
    fun tokenSyncStatusesUseSyncWording() {
        assertEquals("正在同步…", RefreshStatusFormatter.tokenRefreshing(hasCachedData = false))
        assertEquals("正在同步… · 显示上次数据", RefreshStatusFormatter.tokenRefreshing(hasCachedData = true))
        assertEquals("同步失败：Windows 暂不可用", RefreshStatusFormatter.tokenFailure("Windows 暂不可用"))
        assertEquals(
            "更新于 15:03 · 同步失败：Windows 暂不可用",
            RefreshStatusFormatter.tokenFailure("Windows 暂不可用", "15:03"),
        )
    }

    @Test
    fun refreshStatusLineRecognizesBothRefreshAndSyncFailures() {
        assertEquals("刷新失败：", refreshStatusErrorMarker("刷新失败：网络连接异常"))
        assertEquals("同步失败：", refreshStatusErrorMarker("同步失败：Windows 暂不可用"))
    }

    @Test
    fun quotaAndTokenAvailabilityStatusesAreExplicit() {
        assertEquals("尚未连接额度来源", RefreshStatusFormatter.quotaNoSource())
        assertEquals("尚未配对 Windows", RefreshStatusFormatter.tokenUnpaired())
        assertEquals("已配对 Windows · 暂无 Token 数据", RefreshStatusFormatter.tokenPairedWithoutData())
    }

    @Test
    fun missingLoadedTimeDoesNotInventOne() {
        assertEquals("尚未更新 · OpenAI", RefreshStatusFormatter.loaded("OpenAI", null))
    }
}
