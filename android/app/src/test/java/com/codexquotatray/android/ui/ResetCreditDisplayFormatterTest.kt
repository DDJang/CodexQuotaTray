package com.codexquotatray.android.ui

import org.junit.Assert.assertEquals
import org.junit.Test
import java.time.ZoneId
import java.util.Locale

class ResetCreditDisplayFormatterTest {
    @Test
    fun expiryUsesTheRequestedDeviceZoneDeterministically() {
        assertEquals(
            "01-01 08:00",
            formatResetCreditExpiry(0L, ZoneId.of("Asia/Shanghai"), Locale.US),
        )
    }

    @Test
    fun remainingTimeUsesTheAuthoritativeNowValue() {
        assertEquals("1 天 2 小时", formatResetCreditRemaining(93_600L, 0L))
        assertEquals("不足 1 分钟", formatResetCreditRemaining(14_429L, 14_400L))
        assertEquals("已到期", formatResetCreditRemaining(14_400L, 14_400L))
    }
}
