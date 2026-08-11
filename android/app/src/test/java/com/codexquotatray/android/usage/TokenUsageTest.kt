package com.codexquotatray.android.usage

import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import com.codexquotatray.android.refresh.AutomaticRefreshReason
import java.io.File
import java.net.SocketTimeoutException
import java.nio.file.Files

class TokenUsageTest {
    @Test fun pairingUriParsesAndPublicOrInvalidEndpointsAreRejected() {
        val deviceId = "123e4567-e89b-12d3-a456-426614174000"
        val pairing = TokenSyncEndpoint.parsePairingUri("codexquota://pair?deviceId=$deviceId&host=192.168.1.10&port=43821&token=secret&name=Desk%20PC")
        assertEquals(deviceId, pairing.deviceId)
        assertEquals("192.168.1.10", pairing.host)
        assertEquals(43821, pairing.port)
        assertEquals("secret", pairing.secret)
        assertEquals("Desk PC", pairing.displayName)
        assertThrows(IllegalArgumentException::class.java) { TokenSyncEndpoint.parsePairingUri("http://192.168.1.10") }
        assertThrows(IllegalArgumentException::class.java) { TokenSyncEndpoint.parsePairingUri("codexquota://pair?host=192.168.1.10&port=43821&token=secret") }
        assertThrows(IllegalArgumentException::class.java) { TokenSyncEndpoint.parsePairingUri("codexquota://pair/path?deviceId=$deviceId&host=192.168.1.10&port=43821&token=secret") }
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

    @Test fun manualSyncAddsForceRefreshQueryWhileNormalSyncDoesNot() {
        val refreshQueries = mutableListOf<String?>()
        val client = client { chain ->
            refreshQueries += chain.request().url.queryParameter("refresh")
            response(chain, 200, fixture())
        }
        val syncClient = TokenUsageSyncClient(client)

        syncClient.sync(pairingWithId())
        syncClient.sync(pairingWithId(), forceRefresh = true)

        assertEquals(listOf(null, "force"), refreshQueries)
    }

    @Test fun onlyManualRefreshReasonRequestsForceScan() {
        assertTrue(shouldForceTokenUsageRefresh(AutomaticRefreshReason.MANUAL))
        assertFalse(shouldForceTokenUsageRefresh(AutomaticRefreshReason.STARTUP))
        assertFalse(shouldForceTokenUsageRefresh(AutomaticRefreshReason.FOREGROUND))
        assertFalse(shouldForceTokenUsageRefresh(AutomaticRefreshReason.SCHEDULED))
    }

    @Test fun clientClassifiesUnauthorizedMalformedUnsupportedAndOffline() {
        val unauthorized = TokenUsageSyncClient(client { response(it, 401, "") })
        assertEquals(TokenUsageFailureKind.PAIRING_INVALID, failure { unauthorized.fetch(pairing()) }.kind)
        val malformed = TokenUsageSyncClient(client { response(it, 200, "not-json") })
        assertEquals(TokenUsageFailureKind.INVALID_RESPONSE, failure { malformed.fetch(pairing()) }.kind)
        val unsupported = TokenUsageSyncClient(client { response(it, 200, fixture(schema = 2)) })
        assertEquals(TokenUsageFailureKind.UNSUPPORTED, failure { unsupported.fetch(pairing()) }.kind)
        val httpError = TokenUsageSyncClient(client { response(it, 503, "") })
        assertEquals(TokenUsageFailureKind.HTTP_ERROR, failure { httpError.fetch(pairing()) }.kind)
        val offline = TokenUsageSyncClient(client { throw SocketTimeoutException("timeout") })
        assertEquals(TokenUsageFailureKind.OFFLINE, failure { offline.fetch(pairing()) }.kind)
    }

    @Test fun directSuccessDoesNotStartDiscovery() {
        var calls = 0
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
                calls++
                return null
            }
        }
        TokenUsageSyncClient(client { response(it, 200, fixture()) }, discovery).sync(pairingWithId())
        assertEquals(0, calls)
    }

    @Test fun offlineFallsBackToMatchingDiscoveryAndUpdatesHost() {
        var discoveryCalls = 0
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
                discoveryCalls++
                return TokenSyncEndpoint.TokenSyncDiscoveryCandidate(deviceId, "192.168.1.11", 43821, "Desk PC")
            }
        }
        val result = TokenUsageSyncClient(client { chain ->
            if (chain.request().url.host == "192.168.1.10") throw SocketTimeoutException("timeout")
            response(chain, 200, fixture())
        }, discovery).sync(pairingWithId())
        assertEquals(1, discoveryCalls)
        assertEquals("192.168.1.11", result.pairing.host)
        assertEquals("Desk PC", result.pairing.displayName)
    }

    @Test fun unauthorizedDoesNotStartDiscovery() {
        var discoveryCalls = 0
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
                discoveryCalls++
                return null
            }
        }
        assertEquals(
            TokenUsageFailureKind.PAIRING_INVALID,
            failure { TokenUsageSyncClient(client { response(it, 401, "") }, discovery).sync(pairingWithId()) }.kind,
        )
        assertEquals(0, discoveryCalls)
    }

    @Test fun httpErrorDoesNotStartDiscovery() {
        var discoveryCalls = 0
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
                discoveryCalls++
                return null
            }
        }
        assertEquals(
            TokenUsageFailureKind.HTTP_ERROR,
            failure { TokenUsageSyncClient(client { response(it, 503, "") }, discovery).sync(pairingWithId()) }.kind,
        )
        assertEquals(0, discoveryCalls)
    }

    @Test fun discoveryMatchingIgnoresOtherDeviceIds() {
        val target = "123e4567-e89b-12d3-a456-426614174000"
        val candidates = listOf(
            TokenSyncEndpoint.TokenSyncDiscoveryCandidate("123e4567-e89b-12d3-a456-426614174001", "192.168.1.11", 43821, "Other"),
            TokenSyncEndpoint.TokenSyncDiscoveryCandidate(target, "192.168.1.12", 43821, "Desk PC"),
        )
        assertEquals("192.168.1.12", TokenSyncEndpoint.chooseDiscoveryCandidate(candidates, target)?.host)
    }

    @Test fun clientRejectsDiscoveryCandidateForAnotherDevice() {
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? =
                TokenSyncEndpoint.TokenSyncDiscoveryCandidate(
                    "123e4567-e89b-12d3-a456-426614174001",
                    "192.168.1.11",
                    43821,
                    "Other",
                )
        }
        val client = TokenUsageSyncClient(client { throw SocketTimeoutException("timeout") }, discovery)

        assertEquals(TokenUsageFailureKind.OFFLINE, failure { client.sync(pairingWithId()) }.kind)
    }

    @Test fun cacheRoundTripUsesAggregateJsonOnlyAndIsBoundToThePairedWindowsDevice() {
        val directory = Files.createTempDirectory("token-usage-cache").toFile()
        try {
            val file = File(directory, "cache.json")
            val cache = TokenUsageCache.forTest(file)
            val value = TokenUsageJson.parse(fixture())
            val deviceA = pairingWithId()
            val deviceB = TokenSyncEndpoint.validated(
                "123e4567-e89b-12d3-a456-426614174001",
                "192.168.1.11",
                43821,
                "another-secret",
            )
            assertTrue(cache.save(deviceA, value))
            assertEquals(value, cache.load(deviceA))
            assertNull(cache.load(deviceB))
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

    private fun pairingWithId() = TokenSyncEndpoint.validated(
        "123e4567-e89b-12d3-a456-426614174000",
        "192.168.1.10",
        43821,
        "secret",
    )

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
