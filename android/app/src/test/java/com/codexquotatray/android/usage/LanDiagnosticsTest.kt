package com.codexquotatray.android.usage

import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.SocketTimeoutException
import java.util.Collections
import java.util.concurrent.Callable
import java.util.concurrent.Executors

class LanDiagnosticsTest {
    @Test fun networkSnapshotBaselineAndNoOpDoNotAdvanceGeneration() {
        LanNetworkEpoch.resetForTest(31L)
        val tracker = LanNetworkSnapshotTracker()
        val baseline = networkSnapshot()

        fun observe(snapshot: LanNetworkContextSnapshot, reason: String) {
            if (tracker.observe(snapshot) == LanNetworkSnapshotUpdate.CHANGED) {
                LanNetworkEpoch.advance(reason, NoOpLanDiagnosticLogger)
            }
        }

        assertEquals(LanNetworkSnapshotUpdate.BASELINE, tracker.observe(baseline))
        assertEquals(31L, LanNetworkEpoch.current())
        observe(baseline, "DUPLICATE_CALLBACK")
        assertEquals(31L, LanNetworkEpoch.current())

        observe(baseline.copy(localIpv4 = "192.168.1.93"), "LAN_CONTEXT_CHANGED")
        assertEquals(32L, LanNetworkEpoch.current())
        LanNetworkEpoch.resetForTest()
    }

    @Test fun networkSnapshotTrackerResetCreatesNewBaseline() {
        val tracker = LanNetworkSnapshotTracker()
        val snapshot = networkSnapshot()

        assertEquals(LanNetworkSnapshotUpdate.BASELINE, tracker.observe(snapshot))
        tracker.reset()
        assertEquals(LanNetworkSnapshotUpdate.BASELINE, tracker.observe(snapshot))
    }

    @Test fun networkEpochAndDebounceCoalesceCallbacksWithoutPersistence() {
        LanNetworkEpoch.resetForTest(11L)
        val messages = mutableListOf<String>()
        val next = LanNetworkEpoch.advance("NETWORK_LOST", LanDiagnosticLogger(messages::add), nowMillis = 123L)
        assertEquals(12L, next)
        assertEquals(12L, LanNetworkEpoch.snapshot().generation)
        assertEquals("NETWORK_LOST", LanNetworkEpoch.snapshot().lastNetworkChangeReason)

        val debounce = LanNetworkRecoveryDebounce()
        assertTrue(debounce.schedule(12L))
        assertFalse(debounce.schedule(13L))
        assertFalse(debounce.consume(12L))
        assertTrue(debounce.consume(13L))
        assertFalse(debounce.consume(13L))
        assertTrue(messages.any { it.contains("generation=12") && it.contains("NETWORK_LOST") })
    }

    @Test fun staleAttemptDoesNotPublishLanSuccessOrEndpoint() {
        LanNetworkEpoch.resetForTest(20L)
        var recorded = false
        val pairing = pairing()
        val store = object : TokenSyncPairingStore {
            override fun load(): TokenSyncPairing = pairing
            override fun save(pairing: TokenSyncPairing): Boolean = true
            override fun recordLanSuccess(expected: TokenSyncPairing, attempt: LanAttemptContext): Boolean {
                recorded = true
                return true
            }
        }
        val error = runCatching {
            TokenUsageSyncClient(
                client = client {
                    LanNetworkEpoch.advance("LINK_PROPERTIES_CHANGED", NoOpLanDiagnosticLogger)
                    response(it, 200, fixture())
                },
                pairingStore = store,
            ).sync(pairing)
        }.exceptionOrNull()

        assertTrue(error?.isLanAttemptStale() == true)
        assertFalse(recorded)
        LanNetworkEpoch.resetForTest()
    }
    @Test fun directFailureAndNsdRetryShareOneTokenAttemptId() {
        val messages = Collections.synchronizedList(mutableListOf<String>())
        val pairing = pairing()
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate =
                TokenSyncEndpoint.TokenSyncDiscoveryCandidate(deviceId, "192.168.1.11", 43821, "Desk")
        }
        val result = TokenUsageSyncClient(
            client = client { chain ->
                if (chain.request().url.host == pairing.host) throw SocketTimeoutException("connect timeout")
                response(chain, 200, fixture())
            },
            discovery = discovery,
            diagnostics = LanDiagnosticLogger(messages::add),
        ).sync(pairing)

        val attemptLines = messages.filter { it.contains("LAN attempt=") }
        val ids = attemptLines.mapNotNull { Regex("LAN attempt=(\\d+)").find(it)?.groupValues?.get(1) }.toSet()
        assertEquals(1, ids.size)
        assertEquals("192.168.1.11", result.pairing.host)
        assertTrue(attemptLines.any { it.contains("stored endpoint") })
        assertTrue(attemptLines.any { it.contains("NSD start") })
        assertTrue(attemptLines.any { it.contains("discovered endpoint") })
        assertTrue(attemptLines.any { it.contains("result=SUCCESS") })
    }

    @Test fun directFailureAndNsdRetryShareOneQuotaAttemptId() {
        val messages = Collections.synchronizedList(mutableListOf<String>())
        val pairing = pairing()
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate =
                TokenSyncEndpoint.TokenSyncDiscoveryCandidate(deviceId, "192.168.1.11", 43821, "Desk")
        }
        WindowsQuotaFallbackClient(
            client = client { chain ->
                if (chain.request().url.host == pairing.host) throw SocketTimeoutException("connect timeout")
                response(chain, 200, quotaFixture())
            },
            discovery = discovery,
            diagnostics = LanDiagnosticLogger(messages::add),
        ).sync(pairing)

        val attemptLines = messages.filter { it.contains("LAN attempt=") }
        val ids = attemptLines.mapNotNull { Regex("LAN attempt=(\\d+)").find(it)?.groupValues?.get(1) }.toSet()
        assertEquals(1, ids.size)
        assertTrue(attemptLines.all { it.contains("channel=quota") })
        assertTrue(attemptLines.any { it.contains("result=SUCCESS") })
    }

    @Test fun successAndFailureSummariesCarryAttemptStateWithoutChangingBusinessError() {
        var recordedFailure: TokenSyncPairing? = null
        val store = object : TokenSyncPairingStore {
            override fun load(): TokenSyncPairing = pairing()
            override fun save(pairing: TokenSyncPairing): Boolean = true
            override fun recordLanFailure(expected: TokenSyncPairing, attempt: LanAttemptContext): Boolean {
                recordedFailure = TokenSyncEndpoint.markLanFailure(expected, attempt, nowMillis = 123L)
                return true
            }
        }
        val success = TokenUsageSyncClient(
            client = client { response(it, 200, fixture()) },
            pairingStore = store,
        ).sync(pairing())
        assertNotNull(success.pairing.lastLanSuccessAtMillis)
        assertEquals("token", success.pairing.lastLanAttemptChannel)
        assertEquals("192.168.1.10:43821", success.pairing.lastLanTargetEndpoint)

        val error = runCatching {
            TokenUsageSyncClient(
                client = client { throw SocketTimeoutException("timed out") },
                pairingStore = store,
            ).sync(pairing())
        }.exceptionOrNull()
        assertTrue(error is TokenUsageException)
        assertEquals("TCP_CONNECT_TIMEOUT", recordedFailure?.lastLanFailurePhase)
        assertEquals(123L, recordedFailure?.lastLanFailureAtMillis)
    }

    @Test fun formatterIncludesKnownMetadataAndRedactsSecrets() {
        val text = AndroidLanDiagnosticsFormatter.format(
            version = "0.10.2",
            pairing = pairing().copy(
                lastSuccessfulSyncAtMillis = 1_600_000_000_000L,
                lastLanSuccessAtMillis = 1_700_000_000_000L,
                lastLanAttemptId = 184L,
                lastLanAttemptChannel = "token",
            ),
            network = LanNetworkDiagnostics(
                networkHandle = "42",
                interfaceName = "wlan0",
                localIpv4 = "192.168.1.92",
                prefixLength = 24,
                gateway = "192.168.1.1",
                routePrefix = "192.168.1.0/24",
                transports = listOf("WIFI"),
                capabilities = listOf("INTERNET", "VALIDATED"),
                ssid = "Desk Wi-Fi",
                bssid = "aa:bb:cc:dd:ee:ff",
                frequencyMhz = 5180,
            ),
            recentEvents = "Authorization: Bearer secret\npairingSecret=secret token=secret\nLAN attempt=184 result=SUCCESS",
            nowMillis = 1_700_000_000_000L,
            epoch = LanNetworkEpochSnapshot(
                generation = 14L,
                lastNetworkChangeAtMillis = 1_700_000_000_000L,
                lastNetworkChangeReason = "LINK_PROPERTIES_CHANGED",
                lastRecoveryAction = "CONTEXT_REFRESHED",
            ),
        )

        assertTrue(text.contains("platform=Android"))
        assertTrue(text.contains("networkHandle=42"))
        assertTrue(text.contains("interface=wlan0"))
        assertTrue(text.contains("route=192.168.1.0/24"))
        assertTrue(text.contains("Recent LAN events:"))
        assertTrue(text.contains("networkGeneration=14"))
        assertTrue(text.contains("lastNetworkChangeReason=LINK_PROPERTIES_CHANGED"))
        assertFalse(text.contains("secret"))
        assertFalse(text.contains("Bearer"))
    }

    @Test fun unavailableNetworkMetadataFormatsWithoutCrashing() {
        val text = AndroidLanDiagnosticsFormatter.format("test", null, LanNetworkDiagnostics(), "", 0L)
        assertTrue(text.contains("networkHandle=unavailable"))
        assertTrue(text.contains("SSID=unavailable"))
    }

    @Test fun formatterDoesNotTreatNonLanSyncAsLanConnectionSuccess() {
        val text = AndroidLanDiagnosticsFormatter.format(
            version = "test",
            pairing = pairing().copy(lastSuccessfulSyncAtMillis = 1_700_000_000_000L),
            network = null,
            recentEvents = "",
            nowMillis = 1_700_000_000_000L,
        )

        assertTrue(text.contains("lastSuccess=unavailable"))
    }

    @Test fun formatterCanReuseTheLastRecordedRouteWithoutProbingTheNetwork() {
        val network = AndroidLanDiagnosticsFormatter.extractNetwork(
            "LAN attempt=184 channel=token route matching=true networkHandle=42 interface=wlan0 " +
                "local=192.168.1.92 prefixLength=24 gateway=192.168.1.1 routePrefix=192.168.1.0/24 " +
                "transports=WIFI capabilities=INTERNET,VALIDATED SSID=DeskWiFi " +
                "BSSID=aa:bb:cc:dd:ee:ff frequency=5180",
        )

        assertEquals("42", network?.networkHandle)
        assertEquals("wlan0", network?.interfaceName)
        assertEquals(24, network?.prefixLength)
        assertEquals("192.168.1.0/24", network?.routePrefix)
    }

    @Test fun attemptIdsRemainUniqueWhenAllocatedConcurrently() {
        val executor = Executors.newFixedThreadPool(8)
        try {
            val futures = (1..100).map { executor.submit(Callable { LanAttemptIds.nextId() }) }
            val ids = futures.map { it.get() }.toSet()
            assertEquals(100, ids.size)
        } finally {
            executor.shutdownNow()
        }
    }

    private fun pairing() = TokenSyncEndpoint.validated(
        "123e4567-e89b-12d3-a456-426614174000",
        "192.168.1.10",
        43821,
        "secret",
    )

    private fun networkSnapshot() = LanNetworkContextSnapshot(
        networkHandle = 42L,
        interfaceName = "wlan0",
        localIpv4 = "192.168.1.92",
        prefixLength = "24",
        gateway = "192.168.1.1",
        routePrefix = "0.0.0.0/0;192.168.1.0/24",
        transports = "WIFI",
        capabilities = "NOT_RESTRICTED,NOT_SUSPENDED",
        lanEligible = true,
    )

    private fun client(interceptor: (Interceptor.Chain) -> Response): OkHttpClient =
        OkHttpClient.Builder().addInterceptor(interceptor).build()

    private fun response(chain: Interceptor.Chain, code: Int, body: String) = Response.Builder()
        .request(chain.request())
        .protocol(Protocol.HTTP_1_1)
        .code(code)
        .message("test")
        .body(body.toResponseBody("application/json".toMediaType()))
        .build()

    private fun fixture() = """
        {
          "schemaVersion": 1,
          "generatedAtUtc": "2026-08-08T12:00:00Z",
          "sourceTimeZone": "Asia/Shanghai",
          "summary": {"todayTokens": 1, "last7DaysTokens": 1, "last30DaysTokens": 1,
            "lifetimeTokens": 1, "peakDailyTokens": 1, "peakDate": "2026-08-08",
            "activeDays": 1, "currentStreak": 1, "longestStreak": 1},
          "days": [{"date": "2026-08-08", "totalTokens": 1,
            "inputTokens": null, "cachedInputTokens": null, "outputTokens": null,
            "reasoningTokens": null}]
        }
    """.trimIndent()

    private fun quotaFixture() = """
        {"schemaVersion":1,"generatedAtUtc":"2026-08-10T12:00:00Z","planType":"plus",
         "quotaState":"available","windows":[{"limitId":"primary","limitName":null,
         "sourceSlot":"primary","usedPercent":28,"remainingPercent":72,
         "windowDurationMins":300,"resetsAt":1900000000,"bucketId":"codex"}]}
    """.trimIndent()
}
