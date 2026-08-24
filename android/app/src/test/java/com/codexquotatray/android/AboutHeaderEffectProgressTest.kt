package com.codexquotatray.android

import org.junit.Assert.assertEquals
import org.junit.Test

class AboutHeaderEffectProgressTest {
    @Test
    fun `header effect progress follows actual scroll distance`() {
        assertEquals(0f, aboutHeaderEffectProgress(0f, 64f), 0f)
        assertEquals(0.25f, aboutHeaderEffectProgress(16f, 64f), 0.001f)
        assertEquals(0.5f, aboutHeaderEffectProgress(32f, 64f), 0.001f)
        assertEquals(1f, aboutHeaderEffectProgress(64f, 64f), 0f)
        assertEquals(1f, aboutHeaderEffectProgress(128f, 64f), 0f)
    }

    @Test
    fun `header effect progress clamps negative and invalid input`() {
        assertEquals(0f, aboutHeaderEffectProgress(-16f, 64f), 0f)
        assertEquals(0f, aboutHeaderEffectProgress(Float.NaN, 64f), 0f)
        assertEquals(0f, aboutHeaderEffectProgress(32f, 0f), 0f)
        assertEquals(0f, aboutHeaderEffectProgress(32f, -64f), 0f)
    }
}
