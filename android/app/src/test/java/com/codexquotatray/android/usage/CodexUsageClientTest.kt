package com.codexquotatray.android.usage

import com.codexquotatray.android.auth.OAuthCredentials
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.After
import org.junit.Before
import org.junit.Test
import java.util.concurrent.TimeUnit

class CodexUsageClientTest {
    private lateinit var server: MockWebServer

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    @Test
    fun productionEndpointsKeepUsageAndResetCreditReadsSeparate() {
        assertEquals("https://chatgpt.com/backend-api/wham/usage", OAuthCredentials.USAGE_URL)
        assertEquals("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", OAuthCredentials.RESET_CREDITS_URL)
    }

    @Test
    fun parsesPrimarySecondaryAndAdditionalWindows() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """
                {
                  "plan_type": "plus",
                  "rate_limit": {
                    "primary_window": {"used_percent": 12, "reset_at": 1900000000, "limit_window_seconds": 18000},
                    "secondary_window": {"used_percent": 45, "reset_at": 1900500000, "limit_window_seconds": 604800}
                  },
                  "additional_rate_limits": [
                    {
                      "limit_name": "Spark",
                      "rate_limit": {
                        "primary_window": {"used_percent": 20, "limit_window_seconds": 3600}
                      }
                    }
                  ]
                }
                """.trimIndent(),
            ),
        )

        val result = client().fetch(credentials())

        assertEquals("plus", result.planType)
        assertEquals("available", result.quotaState)
        assertEquals(3, result.windows.size)
        assertEquals(listOf(88, 55, 80), result.windows.map { it.remainingPercent })
        assertEquals(listOf(300L, 10_080L, 60L), result.windows.map { it.windowDurationMins })
        assertEquals("Spark", result.windows[2].limitName)
        assertEquals(listOf("codex", "codex", "Spark"), result.windows.map { it.bucketId })
        assertNull(result.resetCredits)

        val request = requireNotNull(server.takeRequest(1, TimeUnit.SECONDS))
        assertEquals("GET", request.method)
        assertEquals("/backend-api/wham/usage", request.path)
        assertEquals("Bearer fake-access", request.getHeader("Authorization"))
        assertEquals("fake-account", request.getHeader("ChatGPT-Account-Id"))
        assertEquals("application/json", request.getHeader("Accept"))
    }

    @Test
    fun usageCountFetchesResetCreditDetailsWithTheSameOAuthHeaders() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """
                {"rate_limit":{"primary_window":{"used_percent":10}},
                 "rate_limit_reset_credits":{"available_count":2}}
                """.trimIndent(),
            ),
        )
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """
                {"credits":[
                  {"id":"credit-1","resetType":"five_hour","status":"available",
                   "grantedAt":1900000000,"expiresAt":1900003600,"title":"Five hour","description":null},
                  {"id":"credit-2","reset_type":"weekly","status":"AVAILABLE",
                   "granted_at":null,"expires_at":null,"title":null,"description":"week"}
                ]}
                """.trimIndent(),
            ),
        )

        val result = client().fetch(credentials())

        assertEquals(2L, result.resetCredits?.availableCount)
        assertEquals(2, result.resetCredits?.credits?.size)
        assertEquals("credit-1", result.resetCredits?.credits?.first()?.id)
        assertEquals("five_hour", result.resetCredits?.credits?.first()?.resetType)
        assertEquals("available", result.resetCredits?.credits?.first()?.status)
        assertEquals(1_900_003_600L, result.resetCredits?.credits?.first()?.expiresAt)
        assertNull(result.resetCredits?.credits?.get(1)?.expiresAt)

        val usageRequest = requireNotNull(server.takeRequest(1, TimeUnit.SECONDS))
        val detailsRequest = requireNotNull(server.takeRequest(1, TimeUnit.SECONDS))
        assertEquals("/backend-api/wham/usage", usageRequest.path)
        assertEquals("/backend-api/wham/rate-limit-reset-credits", detailsRequest.path)
        assertEquals("Bearer fake-access", detailsRequest.getHeader("Authorization"))
        assertEquals("fake-account", detailsRequest.getHeader("ChatGPT-Account-Id"))
    }

    @Test
    fun nullResetCreditSummaryDoesNotChangeUsageSuccess() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"rate_limit":{"primary_window":{"used_percent":10}},"rate_limit_reset_credits":null}""",
            ),
        )

        val result = client().fetch(credentials())

        assertEquals("available", result.quotaState)
        assertTrue(result.resetCredits != null)
        assertNull(result.resetCredits?.availableCount)
        assertEquals("/backend-api/wham/usage", requireNotNull(server.takeRequest(1, TimeUnit.SECONDS)).path)
        assertNull(server.takeRequest(100, TimeUnit.MILLISECONDS))
    }

    @Test
    fun detailFailureKeepsUsageSuccessfulAndAuthoritativeCount() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"rate_limit":{"primary_window":{"used_percent":10}},"rate_limit_reset_credits":{"available_count":2}}""",
            ),
        )
        server.enqueue(MockResponse().setResponseCode(503).setBody("busy"))

        val result = client().fetch(credentials())

        assertEquals("available", result.quotaState)
        assertEquals(2L, result.resetCredits?.availableCount)
        assertNull(result.resetCredits?.credits)
    }

    @Test
    fun invalidDetailsJsonKeepsUsageSuccessfulAndAuthoritativeCount() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"rate_limit":{"primary_window":{"used_percent":10}},"rate_limit_reset_credits":{"available_count":2}}""",
            ),
        )
        server.enqueue(MockResponse().setResponseCode(200).setBody("not-json"))

        val result = client().fetch(credentials())

        assertEquals("available", result.quotaState)
        assertEquals(2L, result.resetCredits?.availableCount)
        assertNull(result.resetCredits?.credits)
    }

    @Test
    fun detailsIOExceptionKeepsUsageSuccessfulAndAuthoritativeCount() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"rate_limit":{"primary_window":{"used_percent":10}},"rate_limit_reset_credits":{"available_count":2}}""",
            ),
        )
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_AT_START))

        val result = client().fetch(credentials())

        assertEquals("available", result.quotaState)
        assertEquals(2L, result.resetCredits?.availableCount)
        assertNull(result.resetCredits?.credits)
    }

    @Test
    fun zeroAvailableCreditsDoesNotReadTheDetailEndpoint() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"rate_limit":{"primary_window":{"used_percent":10}},"rate_limit_reset_credits":{"available_count":0}}""",
            ),
        )

        val result = client().fetch(credentials())

        assertEquals(0L, result.resetCredits?.availableCount)
        assertNull(result.resetCredits?.credits)
        assertEquals("/backend-api/wham/usage", requireNotNull(server.takeRequest(1, TimeUnit.SECONDS)).path)
        assertNull(server.takeRequest(100, TimeUnit.MILLISECONDS))
    }

    @Test
    fun detailsArrayMayBeEmptyAndRemainsDistinctFromUnavailableDetails() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"rate_limit":{"primary_window":{"used_percent":10}},"rate_limit_reset_credits":{"available_count":2}}""",
            ),
        )
        server.enqueue(MockResponse().setResponseCode(200).setBody("{\"credits\":[]}"))

        val result = client().fetch(credentials())

        assertEquals(2L, result.resetCredits?.availableCount)
        assertTrue(result.resetCredits?.credits?.isEmpty() == true)
    }

    @Test
    fun malformedOptionalResetCreditSummaryDoesNotInvalidateBaseQuota() {
        val values = listOf<Any?>(
            JSONObject(),
            JSONObject().put("available_count", JSONObject().put("unexpected", true)),
            JSONObject().put("available_count", "not-a-number"),
        )

        values.forEach { value ->
            val payload = JSONObject()
                .put(
                    "rate_limit",
                    JSONObject().put(
                        "primary_window",
                        JSONObject().put("used_percent", 10),
                    ),
                )
                .put("rate_limit_reset_credits", value)

            val result = client().parseUsage(payload, nowMillis = 1_900_000_000L)

            assertEquals("available", result.quotaState)
            assertEquals(1, result.windows.size)
        }
    }

    @Test
    fun missingWindowFieldsRemainUnknown() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """
                {"rate_limit":{"primary_window":{"used_percent":null,"reset_at":null}}}
                """.trimIndent(),
            ),
        )

        val result = client().fetch(credentials())

        assertEquals("available", result.quotaState)
        assertEquals(1, result.windows.size)
        assertNull(result.windows.single().usedPercent)
        assertNull(result.windows.single().remainingPercent)
        assertNull(result.windows.single().resetsAt)
    }

    @Test
    fun presentRateLimitWithNoWindowsIsExplicitlyZeroWindows() {
        server.enqueue(MockResponse().setResponseCode(200).setBody("{\"rate_limit\":{}}"))

        val result = client().fetch(credentials())

        assertEquals("zero_windows", result.quotaState)
        assertTrue(result.windows.isEmpty())
    }

    @Test
    fun malformedJsonIsAnInvalidResponse() {
        server.enqueue(MockResponse().setResponseCode(200).setBody("not-json"))

        val error = runCatching { client().fetch(credentials()) }.exceptionOrNull() as? UsageException

        assertEquals(UsageFailureKind.INVALID_RESPONSE, error?.kind)
    }

    @Test
    fun authenticationAndServerErrorsRemainDistinct() {
        assertFailure(401, UsageFailureKind.UNAUTHORIZED)
        assertFailure(403, UsageFailureKind.UNAUTHORIZED)
        assertFailure(500, UsageFailureKind.SERVER)
    }

    @Test
    fun timeoutIsReportedAsNetworkFailure() {
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setBody("{}")
                .setBodyDelay(1, TimeUnit.SECONDS),
        )
        val shortClient = OkHttpClient.Builder()
            .connectTimeout(1, TimeUnit.SECONDS)
            .readTimeout(100, TimeUnit.MILLISECONDS)
            .callTimeout(250, TimeUnit.MILLISECONDS)
            .build()

        val error = runCatching {
            CodexUsageClient(shortClient, server.url("/usage").toString()).fetch(credentials())
        }.exceptionOrNull() as? UsageException

        assertEquals(UsageFailureKind.NETWORK, error?.kind)
    }

    @Test
    fun quotaTimeoutProfileUsesShortDirectTimeoutsAndPairedWifiOverride() {
        val defaultClient = CodexUsageClient.defaultClient()
        val pairedWifiClient = CodexUsageClient().clientFor(
            QuotaNetworkTimeouts.directCallTimeoutMillis(windowsPairingOnWifi = true),
        )

        assertEquals(QuotaNetworkTimeouts.DIRECT_CONNECT_TIMEOUT_MILLIS.toInt(), defaultClient.connectTimeoutMillis)
        assertEquals(QuotaNetworkTimeouts.DIRECT_READ_TIMEOUT_MILLIS.toInt(), defaultClient.readTimeoutMillis)
        assertEquals(QuotaNetworkTimeouts.DIRECT_CALL_TIMEOUT_MILLIS.toInt(), defaultClient.callTimeoutMillis)
        assertEquals(QuotaNetworkTimeouts.DIRECT_PAIRED_WIFI_CALL_TIMEOUT_MILLIS.toInt(), pairedWifiClient.callTimeoutMillis)
        assertEquals(
            QuotaNetworkTimeouts.DIRECT_CALL_TIMEOUT_MILLIS,
            QuotaNetworkTimeouts.directCallTimeoutMillis(windowsPairingOnWifi = false),
        )
    }

    @Test
    fun additionalWindowIdentityDoesNotDependOnArrayOrder() {
        val ordered = client().parseUsage(additionalPayload(listOf("alpha", "beta")), 1L)
        val reversed = client().parseUsage(additionalPayload(listOf("beta", "alpha")), 1L)

        val orderedIds = ordered.windows
            .filter { it.limitName != null }
            .associate { it.limitName!! to it.limitId }
        val reversedIds = reversed.windows
            .filter { it.limitName != null }
            .associate { it.limitName!! to it.limitId }

        assertEquals(orderedIds, reversedIds)
        assertTrue(orderedIds.getValue("alpha")!!.contains("alpha"))
        assertTrue(orderedIds.getValue("beta")!!.contains("beta"))
    }

    private fun assertFailure(code: Int, kind: UsageFailureKind) {
        server.enqueue(MockResponse().setResponseCode(code).setBody("{\"error\":\"fake\"}"))
        val error = runCatching { client().fetch(credentials()) }.exceptionOrNull() as? UsageException
        assertEquals(kind, error?.kind)
        assertEquals(code, error?.statusCode)
    }

    private fun client(): CodexUsageClient =
        CodexUsageClient(
            usageUrl = server.url("/backend-api/wham/usage").toString(),
            resetCreditsUrl = server.url("/backend-api/wham/rate-limit-reset-credits").toString(),
        )

    private fun additionalPayload(order: List<String>): JSONObject = JSONObject()
        .put(
            "rate_limit",
            JSONObject().put(
                "primary_window",
                JSONObject().put("used_percent", 10).put("limit_window_seconds", 300),
            ),
        )
        .put(
            "additional_rate_limits",
            JSONArray().apply {
                order.forEach { name ->
                    put(
                        JSONObject()
                            .put("limit_id", name)
                            .put("limit_name", name)
                            .put(
                                "rate_limit",
                                JSONObject().put(
                                    "primary_window",
                                    JSONObject().put("used_percent", 20),
                                ),
                            ),
                    )
                }
            },
        )

    private fun credentials() = OAuthCredentials(
        accessToken = "fake-access",
        refreshToken = "fake-refresh",
        accountId = "fake-account",
    )
}
