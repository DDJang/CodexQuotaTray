package com.codexquotatray.android.usage

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.file.Files
import java.time.ZoneId
import java.time.ZonedDateTime
import com.codexquotatray.android.auth.OAuthCredentials

class OpenAIAccountTokenUsageTest {
    private val now = ZonedDateTime.of(2026, 8, 23, 12, 0, 0, 0, ZoneId.of("Asia/Shanghai"))

    @Test fun nullableProfileAndMissingTodayRemainUnavailable() {
        val parsed = CodexUsageClient().parseTokenProfile(JSONObject("""
            {
              "stats": {
                "summary": { "lifetime_tokens": null },
                "daily_usage_buckets": [
                  { "start_date": "2026-08-22", "tokens": 10 }
                ]
              }
            }
        """), now)

        assertNull(parsed.summary.todayTokens)
        assertEquals(10L, parsed.summary.last7DaysTokens)
        assertEquals(10L, parsed.summary.last30DaysTokens)
        assertNull(parsed.summary.lifetimeTokens)
        assertEquals(DataTransport.OPENAI, parsed.transport)
        assertEquals(TokenUsageScope.ACCOUNT, parsed.scope)
        assertTrue(parsed.days.all {
            it.inputTokens == null && it.cachedInputTokens == null &&
                it.outputTokens == null && it.reasoningTokens == null
        })
    }

    @Test fun absentSummaryAndBucketsStayUnavailableInsteadOfZero() {
        val parsed = CodexUsageClient().parseTokenProfile(JSONObject("{}"), now)
        assertNull(parsed.summary.todayTokens)
        assertNull(parsed.summary.last7DaysTokens)
        assertNull(parsed.summary.last30DaysTokens)
        assertNull(parsed.summary.lifetimeTokens)
        assertNull(parsed.summary.activeDays)
        assertTrue(parsed.days.isEmpty())
    }

    @Test fun unauthorizedProfileReadRefreshesExactlyOnce() {
        var reads = 0
        var refreshes = 0
        val value = retryUnauthorizedOnce(
            firstAttempt = {
                reads++
                throw UsageException(UsageFailureKind.UNAUTHORIZED, "unauthorized")
            },
            refresh = {
                refreshes++
                OAuthCredentials("refreshed", "refresh")
            },
            retry = {
                reads++
                "ok"
            },
        )
        assertEquals("ok", value)
        assertEquals(2, reads)
        assertEquals(1, refreshes)
    }

    @Test fun lanMetadataMapsScopesAndLegacyPayloadMigratesToWindowsLocal() {
        val legacy = TokenUsageJson.parse(wireJson())
        assertEquals(DataTransport.WINDOWS, legacy.transport)
        assertEquals(TokenUsageScope.LOCAL, legacy.scope)

        val account = TokenUsageJson.parse(wireJson("\"source\":\"OAuth\",\"scope\":\"Account\","))
        assertEquals(DataTransport.WINDOWS, account.transport)
        assertEquals(TokenUsageScope.ACCOUNT, account.scope)
        assertEquals("OAuth", account.source)
    }

    @Test fun cacheKeepsActualTransportAndNeverRestoresItForAnotherProvider() {
        val root = Files.createTempDirectory("token-source-cache").toFile()
        try {
            val cache = TokenUsageCache.forTest(root.resolve("cache.json"))
            val pairing = TokenSyncEndpoint.validated(
                "123e4567-e89b-12d3-a456-426614174000",
                "192.168.1.10",
                43821,
                "secret",
            )
            val openAI = snapshot(DataTransport.OPENAI, TokenUsageScope.ACCOUNT)
            assertTrue(cache.saveOpenAI(openAI))
            assertNull(cache.loadForAvailableSources(pairing, hasOAuth = false))
            assertEquals(openAI, cache.loadForAvailableSources(null, hasOAuth = true))

            val local = snapshot(DataTransport.WINDOWS, TokenUsageScope.LOCAL)
            assertTrue(cache.save(pairing, local))
            assertNull(cache.loadForAvailableSources(null, hasOAuth = true))
            assertEquals(local, cache.loadForAvailableSources(pairing, hasOAuth = false))
        } finally {
            root.deleteRecursively()
        }
    }

    private fun wireJson(metadata: String = "") = """
        {
          "schemaVersion":1,
          $metadata
          "generatedAtUtc":"2026-08-23T00:00:00Z",
          "sourceTimeZone":"UTC",
          "summary":{
            "todayTokens":1,"last7DaysTokens":2,"last30DaysTokens":3,
            "lifetimeTokens":4,"peakDailyTokens":4,"peakDate":"2026-08-23",
            "activeDays":1,"currentStreak":1,"longestStreak":1
          },
          "days":[]
        }
    """
}
