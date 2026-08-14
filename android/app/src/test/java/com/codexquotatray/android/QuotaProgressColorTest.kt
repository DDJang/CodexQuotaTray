package com.codexquotatray.android

import androidx.compose.ui.graphics.toArgb
import org.junit.Assert.assertEquals
import org.junit.Test

class QuotaProgressColorTest {
    @Test
    fun `high medium and low remaining map to green yellow and red`() {
        assertEquals(0xff35e66b.toInt(), quotaProgressColor(100).toArgb())
        assertEquals(0xffffd84d.toInt(), quotaProgressColor(50).toArgb())
        assertEquals(0xffff4d5d.toInt(), quotaProgressColor(0).toArgb())
    }

    @Test
    fun `remaining percentage is clamped before interpolation`() {
        assertEquals(quotaProgressColor(100), quotaProgressColor(150))
        assertEquals(quotaProgressColor(0), quotaProgressColor(-20))
        assertEquals(0xff35e66b.toInt(), quotaProgressArgb(110))
        assertEquals(0xffff4d5d.toInt(), quotaProgressArgb(-10))
    }

    @Test
    fun `pure argb helper preserves continuous red yellow green interpolation`() {
        assertEquals(0xffff9355.toInt(), quotaProgressArgb(25))
        assertEquals(0xff9adf5c.toInt(), quotaProgressArgb(75))
    }

    @Test
    fun `remaining percentage maps to normalized progress`() {
        assertEquals(1f, quotaProgress(100))
        assertEquals(0.5f, quotaProgress(50))
        assertEquals(0f, quotaProgress(0))
        assertEquals(1f, quotaProgress(120))
        assertEquals(0f, quotaProgress(-20))
    }
}
