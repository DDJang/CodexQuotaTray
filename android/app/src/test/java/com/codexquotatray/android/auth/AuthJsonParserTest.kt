package com.codexquotatray.android.auth

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class AuthJsonParserTest {
    @Test
    fun parsesLegacyTokensWithoutChangingTheLegacyFile() {
        val credentials = AuthJsonParser.parse(
            """
            {
              "tokens": {
                "access_token": "fake-access",
                "refresh_token": "fake-refresh",
                "id_token": "fake-id",
                "account_id": "fake-account",
                "access_token_expires_at": 1900000000
              },
              "last_refresh": "2026-08-07T00:00:00Z"
            }
            """.trimIndent(),
        )

        requireNotNull(credentials)
        assertEquals("fake-access", credentials.accessToken)
        assertEquals("fake-refresh", credentials.refreshToken)
        assertEquals("fake-id", credentials.idToken)
        assertEquals("fake-account", credentials.accountId)
        assertEquals(1_900_000_000L, credentials.accessTokenExpiresAtSeconds)
        assertEquals(testRefreshMillis(), credentials.lastRefreshMillis)
    }

    @Test
    fun malformedOrIncompleteLegacyAuthIsIgnored() {
        assertNull(AuthJsonParser.parse("not json"))
        assertNull(AuthJsonParser.parse("{\"tokens\":{\"refresh_token\":\"only-refresh\"}}"))
    }

    private fun testRefreshMillis(): Long =
        java.time.Instant.parse("2026-08-07T00:00:00Z").toEpochMilli()
}
