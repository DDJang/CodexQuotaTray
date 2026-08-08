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

    @Test
    fun bearerAuthorizationTokensAreRemoved() {
        val first = "eyJheader.payload.signature"
        val second = "second-bearer-token"
        val sanitized = AppLogSanitizer.sanitize(
            "Authorization: Bearer $first Authorization=Bearer $second",
        )

        assertFalse(sanitized.contains(first))
        assertFalse(sanitized.contains(second))
    }

    @Test
    fun jsonTokenFieldsAndCookiesAreRemoved() {
        val access = "json-access-secret"
        val refresh = "json-refresh-secret"
        val deviceCode = "device-code-secret"
        val cookie = "session=secret-cookie"
        val sanitized = AppLogSanitizer.sanitize(
            "{\"access_token\":\"$access\",\"refresh_token\":\"$refresh\",\"device_code\":\"$deviceCode\"} Cookie: $cookie",
        )

        assertFalse(sanitized.contains(access))
        assertFalse(sanitized.contains(refresh))
        assertFalse(sanitized.contains(deviceCode))
        assertFalse(sanitized.contains(cookie))
    }
}
