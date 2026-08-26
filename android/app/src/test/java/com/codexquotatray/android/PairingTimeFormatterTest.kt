package com.codexquotatray.android

import java.time.Instant
import java.time.ZoneId
import java.util.Locale
import org.junit.Assert.assertEquals
import org.junit.Test

class PairingTimeFormatterTest {
    private val zone = ZoneId.of("Asia/Shanghai")
    private val now = Instant.parse("2026-08-26T11:30:00Z").toEpochMilli()

    @Test fun recentPairingTimeUsesTodayYesterdayAndCompactDateLabels() {
        assertEquals(
            "今天 19:21",
            PairingTimeFormatter.format(
                Instant.parse("2026-08-26T11:21:00Z").toEpochMilli(),
                now,
                zone,
                Locale.ROOT,
            ),
        )
        assertEquals(
            "昨天 22:06",
            PairingTimeFormatter.format(
                Instant.parse("2026-08-25T14:06:00Z").toEpochMilli(),
                now,
                zone,
                Locale.ROOT,
            ),
        )
        assertEquals(
            "08-24 16:30",
            PairingTimeFormatter.format(
                Instant.parse("2026-08-24T08:30:00Z").toEpochMilli(),
                now,
                zone,
                Locale.ROOT,
            ),
        )
    }
}
