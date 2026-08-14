package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Test

class ResetTimeFormatterTest {
    @Test
    fun multiDayResetIncludesDaysHoursAndMinutes() {
        assertEquals(
            "6 天 23 小时 58 分钟",
            formatResetRemaining(6 * 86_400L + 23 * 3_600L + 58 * 60L + 12L),
        )
    }

    @Test
    fun shorterResetKeepsHoursAndMinutes() {
        assertEquals("19 小时 2 分钟", formatResetRemaining(19 * 3_600L + 2 * 60L + 20L))
    }

    @Test
    fun expiredAndSubMinuteResetRemainExplicit() {
        assertEquals("已到期或正在刷新", formatResetRemaining(0L))
        assertEquals("不足 1 分钟", formatResetRemaining(59L))
    }
}
