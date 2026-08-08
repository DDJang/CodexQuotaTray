package com.codexquotatray.android.usage

import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.net.SocketTimeoutException
import java.nio.file.Files

class TokenUsageTest {
    @Test fun pairingUriParsesAndPublicOrInvalidEndpointsAreRejected() {
        val pairing = TokenSyncEndpoint.parsePairingUri("codexquota://pair?host=192.168.1.10&port=43821&token=secret")
        assertEquals("192.168.1.10", pairing.host)
        assertEquals(43821, pairing.port)
        assertEquals("secret", pairing.secret)
        assertThrows(IllegalArgumentException::class.java) { TokenSyncEndpoint.parsePairingUri("http://192.168.1.10") }
        assertThrows(IllegalArgumentException::class.java) { TokenSyncEndpoint.validated("8.8.8.8", 43821, "secret") }
        assertThrows(IllegalArgumentException::class.java) { TokenSyncEndpoint.validated("127.0.0.1", 43821, "secret") }
        assertTrue(TokenSyncEndpoint.isPrivateIpv4("10.1.2.3"))
        assertTrue(TokenSyncEndpoint.isPrivateIpv4("172.31.0.1"))
        assertFalse(TokenSyncEndpoint.isPrivateIpv4("172.32.0.1"))
    }

    @Test fun clientParses200AndSendsBearer() {
        var authorization: String? = null
        val client = client { chain ->
            authorization = chain.request().header("Authorization")
            response(chain, 200, fixture())
        }
        val snapshot = TokenUsageSyncClient(client).fetch(pairing())
        assertEquals("Bearer secret", authorization)
        assertEquals(1234L, snapshot.summary.todayTokens)
        assertEquals(1, snapshot.days.size)
    }

    @Test fun clientClassifiesUnauthorizedMalformedUnsupportedAndOffline() {
        val unauthorized = TokenUsageSyncClient(client { response(it, 401, "") })
        assertEquals(TokenUsageFailureKind.PAIRING_INVALID, failure { unauthorized.fetch(pairing()) }.kind)
        val malformed = TokenUsageSyncClient(client { response(it, 200, "not-json") })
        assertEquals(TokenUsageFailureKind.INVALID_RESPONSE, failure { malformed.fetch(pairing()) }.kind)
        val unsupported = TokenUsageSyncClient(client { response(it, 200, fixture(schema = 2)) })
        assertEquals(TokenUsageFailureKind.UNSUPPORTED, failure { unsupported.fetch(pairing()) }.kind)
        val offline = TokenUsageSyncClient(client { throw SocketTimeoutException("timeout") })
        assertEquals(TokenUsageFailureKind.OFFLINE, failure { offline.fetch(pairing()) }.kind)
    }

    @Test fun cacheRoundTripUsesAggregateJsonOnly() {
        val directory = Files.createTempDirectory("token-usage-cache").toFile()
        try {
            val file = File(directory, "cache.json")
            val cache = TokenUsageCache.forTest(file)
            val value = TokenUsageJson.parse(fixture())
            assertTrue(cache.save(value))
            assertEquals(value, cache.load())
            val raw = file.readText()
            assertFalse(raw.contains("prompt", ignoreCase = true))
            assertFalse(raw.contains("session", ignoreCase = true))
        } finally {
            directory.deleteRecursively()
        }
    }

    @Test fun tokenFormatterUsesStableCompactUnitsForLongValues() {
        assertEquals("999", TokenFormatter.format(999))
        assertEquals("1K", TokenFormatter.format(1_000))
        assertEquals("1M", TokenFormatter.format(1_000_000))
        assertEquals("1B", TokenFormatter.format(1_000_000_000))
        assertEquals("9223372037B", TokenFormatter.format(Long.MAX_VALUE))
    }

    @Test fun heatmapBucketsUseNonZeroDistributionAndPreserveZero() {
        val values = listOf(1L, 10L, 100L, 1_000L, 1_000_000_000L)
        assertEquals(0, HeatmapBuckets.bucket(0, values))
        assertEquals(1, HeatmapBuckets.bucket(1, values))
        assertEquals(4, HeatmapBuckets.bucket(1_000_000_000L, values))
        assertTrue(HeatmapBuckets.bucket(100, values) >= 2)
    }

    private fun pairing() = TokenSyncPairing("192.168.1.10", 43821, "secret")

    private fun failure(block: () -> Unit): TokenUsageException =
        assertThrows(TokenUsageException::class.java, block)

    private fun client(interceptor: (Interceptor.Chain) -> Response): OkHttpClient =
        OkHttpClient.Builder().addInterceptor(interceptor).build()

    private fun response(chain: Interceptor.Chain, code: Int, body: String) = Response.Builder()
        .request(chain.request())
        .protocol(Protocol.HTTP_1_1)
        .code(code)
        .message("test")
        .body(body.toResponseBody("application/json".toMediaType()))
        .build()

    private fun fixture(schema: Int = 1) = """
        {
          "schemaVersion": $schema,
          "generatedAtUtc": "2026-08-08T12:00:00Z",
          "sourceTimeZone": "Asia/Shanghai",
          "summary": {
            "todayTokens": 1234,
            "last7DaysTokens": 2000,
            "last30DaysTokens": 3000,
            "lifetimeTokens": 4000,
            "peakDailyTokens": 1234,
            "peakDate": "2026-08-08",
            "activeDays": 4,
            "currentStreak": 2,
            "longestStreak": 3
          },
          "days": [{
            "date": "2026-08-08",
            "totalTokens": 1234,
            "inputTokens": null,
            "cachedInputTokens": null,
            "outputTokens": null,
            "reasoningTokens": null
          }]
        }
    """.trimIndent()
}
