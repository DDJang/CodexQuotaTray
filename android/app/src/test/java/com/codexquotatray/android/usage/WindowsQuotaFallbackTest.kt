package com.codexquotatray.android.usage

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.quota.LanAvailability
import com.codexquotatray.android.quota.QuotaReadException
import com.codexquotatray.android.quota.QuotaReadFailureKind
import com.codexquotatray.android.quota.WindowsQuotaFallbackResolver
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Test
import java.net.SocketTimeoutException

class WindowsQuotaFallbackTest {
    @Test fun directQuotaSuccessDoesNotAccessWindows() {
        var fallbackCalls = 0
        val result = resolver(fallback = { fallbackCalls++; success() }).fetch { success() }

        assertEquals(QuotaSource.DIRECT, result.source)
        assertEquals(0, fallbackCalls)
    }

    @Test fun directNetworkFailureWithoutPairingKeepsPrimaryError() {
        val primary = networkFailure()
        val failure = assertThrows(QuotaReadException::class.java) {
            resolver(pairing = null).fetch { throw primary }
        }

        assertSame(primary, failure)
    }

    @Test fun directNetworkFailureWithPairedWindowsReturnsWindowsQuota() {
        val result = resolver().fetch { throw networkFailure() }

        assertEquals(QuotaSource.WINDOWS, result.source)
        assertEquals(72, result.windows.single().remainingPercent)
    }

    @Test fun windowsQuotaWorksWithoutAndroidOAuthOrDirectAttempt() {
        var fallbackCalls = 0
        val result = resolver(fallback = { fallbackCalls++; windowsSuccess() }).fetchWindowsOnly()

        assertEquals(QuotaSource.WINDOWS, result.source)
        assertEquals(1, fallbackCalls)
    }

    @Test fun windowsOnlyQuotaClassifiesLanAndPairingFailures() {
        val offline = assertThrows(QuotaReadException::class.java) {
            resolver(lanAvailable = false).fetchWindowsOnly()
        }
        assertEquals(QuotaReadFailureKind.NETWORK, offline.kind)

        val missing = assertThrows(QuotaReadException::class.java) {
            resolver(pairing = null).fetchWindowsOnly()
        }
        assertEquals(QuotaReadFailureKind.LOGIN_REQUIRED, missing.kind)
    }

    @Test fun offlineWindowsFallbackKeepsPrimaryNetworkError() {
        val primary = networkFailure()
        val failure = assertThrows(QuotaReadException::class.java) {
            resolver(fallback = { throw WindowsQuotaFallbackException(WindowsQuotaFallbackFailureKind.OFFLINE, "offline") })
                .fetch { throw primary }
        }

        assertSame(primary, failure)
    }

    @Test fun loginRequiredAndInvalidResponseNeverAccessWindows() {
        for (kind in listOf(
            QuotaReadFailureKind.LOGIN_REQUIRED,
            QuotaReadFailureKind.INVALID_RESPONSE,
            QuotaReadFailureKind.SERVER,
        )) {
            var fallbackCalls = 0
            val primary = QuotaReadException(kind, "primary")
            val failure = assertThrows(QuotaReadException::class.java) {
                resolver(fallback = { fallbackCalls++; success() }).fetch { throw primary }
            }
            assertSame(primary, failure)
            assertEquals(0, fallbackCalls)
        }
    }

    @Test fun staleHostDiscoversSameDeviceUpdatesHostAndRetriesOnce() {
        var discoveryCalls = 0
        var discoveryTimeoutMs: Long? = null
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
                discoveryCalls++
                discoveryTimeoutMs = timeoutMs
                return TokenSyncEndpoint.TokenSyncDiscoveryCandidate(deviceId, "192.168.1.11", 43821, "Desk")
            }
        }
        val fallback = WindowsQuotaFallbackClient(client { chain ->
            if (chain.request().url.host == "192.168.1.10") throw SocketTimeoutException("offline")
            response(chain, 200, quotaFixture())
        }, discovery)

        val result = fallback.sync(pairing())

        assertEquals(1, discoveryCalls)
        assertEquals(QuotaNetworkTimeouts.WINDOWS_DNS_SD_TIMEOUT_MILLIS, discoveryTimeoutMs)
        assertEquals("192.168.1.11", result.pairing.host)
        assertEquals(QuotaSource.WINDOWS, result.quota.source)
    }

    @Test fun windowsQuotaTimeoutProfileIsShortAndDoesNotUseTokenUsageTimeouts() {
        val client = WindowsQuotaFallbackClient.defaultClient()

        assertEquals(QuotaNetworkTimeouts.WINDOWS_CONNECT_TIMEOUT_MILLIS.toInt(), client.connectTimeoutMillis)
        assertEquals(QuotaNetworkTimeouts.WINDOWS_READ_TIMEOUT_MILLIS.toInt(), client.readTimeoutMillis)
        assertEquals(QuotaNetworkTimeouts.WINDOWS_CALL_TIMEOUT_MILLIS.toInt(), client.callTimeoutMillis)
    }

    @Test fun relocatedHostIsPersistedThroughTheExistingPairingStore() {
        var saved: TokenSyncPairing? = null
        val original = pairing()
        val relocated = original.copy(lastKnownHost = "192.168.1.11")
        val resolver = WindowsQuotaFallbackResolver(
            pairingStore = object : TokenSyncPairingStore {
                override fun load(): TokenSyncPairing = original
                override fun save(pairing: TokenSyncPairing): Boolean {
                    saved = pairing
                    return true
                }
            },
            lanAvailability = object : LanAvailability { override fun isAvailable() = true },
            fallbackClient = object : WindowsQuotaFallback {
                override fun sync(pairing: TokenSyncPairing) =
                    WindowsQuotaFallbackResult(windowsSuccess(), relocated)
            },
        )

        resolver.fetch { throw networkFailure() }

        assertEquals("192.168.1.11", saved?.host)
    }

    @Test fun pairingUnauthorizedDoesNotTriggerDiscovery() {
        var discoveryCalls = 0
        val discovery = object : TokenSyncDiscovery {
            override fun find(deviceId: String, timeoutMs: Long): TokenSyncEndpoint.TokenSyncDiscoveryCandidate? {
                discoveryCalls++
                return null
            }
        }

        val failure = assertThrows(WindowsQuotaFallbackException::class.java) {
            WindowsQuotaFallbackClient(client { response(it, 401, "") }, discovery).sync(pairing())
        }

        assertEquals(WindowsQuotaFallbackFailureKind.PAIRING_INVALID, failure.kind)
        assertEquals(0, discoveryCalls)
    }

    @Test fun nullWindowsFieldsRemainUnknownInsteadOfBecomingZero() {
        val result = WindowsQuotaJson.parse(
            """
            {"schemaVersion":1,"generatedAtUtc":"2026-08-10T12:00:00Z","planType":null,
             "quotaState":"available","windows":[{"limitId":null,"limitName":null,
             "sourceSlot":"primary","usedPercent":null,"remainingPercent":null,
             "windowDurationMins":null,"resetsAt":null}]}
            """.trimIndent(),
        )

        val window = result.windows.single()
        assertNull(window.usedPercent)
        assertNull(window.remainingPercent)
        assertNull(window.windowDurationMins)
        assertNull(window.resetsAt)
    }

    private fun resolver(
        pairing: TokenSyncPairing? = pairing(),
        lanAvailable: Boolean = true,
        fallback: () -> DirectQuotaResult = ::windowsSuccess,
    ): WindowsQuotaFallbackResolver = WindowsQuotaFallbackResolver(
        pairingStore = object : TokenSyncPairingStore {
            override fun load(): TokenSyncPairing? = pairing
            override fun save(pairing: TokenSyncPairing): Boolean = true
        },
        lanAvailability = object : LanAvailability {
            override fun isAvailable(): Boolean = lanAvailable
        },
        fallbackClient = object : WindowsQuotaFallback {
            override fun sync(pairing: TokenSyncPairing): WindowsQuotaFallbackResult =
                WindowsQuotaFallbackResult(fallback(), pairing)
        },
    )

    private fun pairing() = TokenSyncEndpoint.validated(
        "123e4567-e89b-12d3-a456-426614174000",
        "192.168.1.10",
        43821,
        "secret",
    )

    private fun success() = DirectQuotaResult(
        planType = "plus",
        windows = listOf(window(72)),
        quotaState = "available",
        updatedAtMillis = 123L,
    )

    private fun windowsSuccess() = success().copy(source = QuotaSource.WINDOWS)

    private fun window(remaining: Int) = QuotaWindow(
        limitId = "primary",
        limitName = null,
        sourceSlot = "primary",
        usedPercent = 100 - remaining,
        remainingPercent = remaining,
        windowDurationMins = 300,
        resetsAt = 1_900_000_000L,
    )

    private fun networkFailure() = QuotaReadException(QuotaReadFailureKind.NETWORK, "network")

    private fun client(interceptor: (Interceptor.Chain) -> Response): OkHttpClient =
        OkHttpClient.Builder().addInterceptor(interceptor).build()

    private fun response(chain: Interceptor.Chain, code: Int, body: String): Response = Response.Builder()
        .request(chain.request())
        .protocol(Protocol.HTTP_1_1)
        .code(code)
        .message("test")
        .body(body.toResponseBody("application/json".toMediaType()))
        .build()

    private fun quotaFixture() =
        """{"schemaVersion":1,"generatedAtUtc":"2026-08-10T12:00:00Z","planType":"plus",
            "quotaState":"available","windows":[{"limitId":"primary","limitName":null,
            "planType":null,"sourceSlot":"primary","usedPercent":28,"remainingPercent":72,
            "percentageReliable":true,"windowDurationMins":300,"resetsAt":1900000000}]}""".trimIndent()
}
