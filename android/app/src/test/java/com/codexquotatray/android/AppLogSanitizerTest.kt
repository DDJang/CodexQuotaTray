package com.codexquotatray.android

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AppLogSanitizerTest {
    @Test
    fun sensitiveValuesAreRemovedFromLogMessages() {
        val sanitized = AppLogSanitizer.sanitize(
            "access_token=access-secret refresh_token:refresh-secret device code:ABCD-1234",
        )

        assertFalse(sanitized.contains("access-secret"))
        assertFalse(sanitized.contains("refresh-secret"))
        assertFalse(sanitized.contains("ABCD-1234"))
        assertTrue(sanitized.contains("[已隐藏]"))
    }
}
