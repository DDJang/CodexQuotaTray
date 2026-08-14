package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Test

class RefreshStatusFormatterTest {
    @Test
    fun quotaLoadedStatusNamesItsSourceAndDataTime() {
        assertEquals("OpenAI · 更新于 15:03", RefreshStatusFormatter.loaded("OpenAI", "15:03"))
        assertEquals("Windows · 更新于 15:03", RefreshStatusFormatter.loaded("Windows", "15:03"))
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
    fun quotaAndTokenAvailabilityStatusesAreExplicit() {
        assertEquals("尚未连接额度来源", RefreshStatusFormatter.quotaNoSource())
        assertEquals("尚未配对 Windows", RefreshStatusFormatter.tokenUnpaired())
        assertEquals("已配对 Windows · 尚无统计数据", RefreshStatusFormatter.tokenPairedWithoutData())
    }

    @Test
    fun missingLoadedTimeDoesNotInventOne() {
        assertEquals("OpenAI · 尚未更新", RefreshStatusFormatter.loaded("OpenAI", null))
    }
}
