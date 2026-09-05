package com.codexquotatray.android.auth

import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class CodexOAuthClientTest {
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
    fun refreshSendsTheExpectedJsonRequestAndKeepsRotatedTokens() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """
                {"access_token":"new-access","refresh_token":"new-refresh","id_token":"new-id"}
                """.trimIndent(),
            ),
        )
        val client = client()

        val result = client.refresh(credentials())
        val request = server.takeRequest()
        val body = JSONObject(request.body.readUtf8())

        assertEquals("POST", request.method)
        assertEquals("/oauth/token", request.path)
        assertEquals("application/json; charset=utf-8", request.getHeader("Content-Type"))
        assertEquals(OAuthCredentials.CLIENT_ID, body.getString("client_id"))
        assertEquals("refresh_token", body.getString("grant_type"))
        assertEquals("old-refresh", body.getString("refresh_token"))
        assertEquals(3, body.length())
        assertTrue(!body.has("scope"))
        assertEquals("new-access", result.accessToken)
        assertEquals("new-refresh", result.refreshToken)
        assertEquals("new-id", result.idToken)
    }

    @Test
    fun refreshFailureMatchesOfficialStatusAndCodeClassification() {
        val cases = listOf(
            Triple(400, "refresh_token_expired", OAuthFailureKind.REFRESH_EXPIRED),
            Triple(403, "REFRESH_TOKEN_REUSED", OAuthFailureKind.REFRESH_REUSED),
            Triple(400, "refresh_token_invalidated", OAuthFailureKind.REFRESH_REVOKED),
            Triple(400, "invalid_grant", OAuthFailureKind.LOGIN_REQUIRED),
            Triple(401, "unknown", OAuthFailureKind.LOGIN_REQUIRED),
            Triple(403, "unknown", OAuthFailureKind.SERVER),
            Triple(403, "invalid_grant", OAuthFailureKind.SERVER),
            Triple(500, "invalid_grant", OAuthFailureKind.SERVER),
        )
        for ((status, code, expected) in cases) {
            for (body in listOf("""{"error":{"code":"$code"}}""", """{"error":"$code"}""", """{"code":"$code"}""")) {
                server.enqueue(MockResponse().setResponseCode(status).setBody(body))
                val error = runCatching { client().refresh(credentials()) }.exceptionOrNull() as OAuthException
                assertEquals("$status $body", expected, error.kind)
                assertEquals(status, error.statusCode)
            }
        }
        for (body in listOf("<html>blocked</html>", "{}", "[]")) {
            server.enqueue(MockResponse().setResponseCode(403).setBody(body))
            val error = runCatching { client().refresh(credentials()) }.exceptionOrNull() as OAuthException
            assertEquals(OAuthFailureKind.SERVER, error.kind)
        }
    }

    @Test
    fun refreshDiagnosticsAreRedactedAndDistinguishRecovery() {
        val logs = mutableListOf<String>()
        val client = CodexOAuthClient(OkHttpClient(), server.url("/").toString(), diagnostics = logs::add)
        server.enqueue(MockResponse().setBody("""{"access_token":"new-access","refresh_token":"new-refresh"}"""))
        client.refresh(credentials(), OAuthRefreshReason.UNAUTHORIZED_RECOVERY)
        assertTrue(logs.any { "reason=UNAUTHORIZED_RECOVERY status=200 error.code=none" in it })
        assertTrue(logs.any { "rotation=true" in it })
        server.enqueue(MockResponse().setResponseCode(403).setBody("""{"error":{"code":"private@example.test-secret"}}"""))
        runCatching { client.refresh(credentials()) }
        assertTrue(logs.any { "reason=PROACTIVE status=403 error.code=unknown_" in it })
        for (secret in listOf("old-refresh", "old-access", "new-refresh", "new-access", "private@example.test-secret")) {
            assertTrue(logs.none { secret in it })
        }
    }

    @Test
    fun refreshKeepsOmittedTokensIncludingAccessWhenOnlyRefreshRotates() {
        server.enqueue(MockResponse().setBody("""{"refresh_token":"new-refresh"}"""))
        val result = client().refresh(credentials())
        assertEquals(credentials().accessToken, result.accessToken)
        assertEquals("new-refresh", result.refreshToken)
        server.enqueue(MockResponse().setBody("""{"access_token":"new-access"}"""))
        assertEquals(credentials().refreshToken, client().refresh(credentials()).refreshToken)
    }

    @Test
    fun refreshTokenReuseIsARecoverableLoginFailure() {
        server.enqueue(
            MockResponse().setResponseCode(400).setBody(
                """{"error":{"code":"refresh_token_reused"}}""",
            ),
        )

        val error = runCatching { client().refresh(credentials()) }.exceptionOrNull() as? OAuthException

        assertEquals(OAuthFailureKind.REFRESH_REUSED, error?.kind)
        assertEquals(400, error?.statusCode)
    }

    @Test
    fun deviceLoginPublishesUserCodeAndUsesCurrentVerificationUrl() {
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"device_auth_id":"device-1","user_code":"ABCD-EFGH","interval":1}""",
            ),
        )
        server.enqueue(MockResponse().setResponseCode(403).setBody("{}"))
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"authorization_code":"authorization-code","code_challenge":"challenge","code_verifier":"verifier"}""",
            ),
        )
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                """{"access_token":"device-access","refresh_token":"device-refresh","id_token":"device-id"}""",
            ),
        )
        val updates = mutableListOf<OAuthLoginUpdate>()

        val result = client().login { updates += it }

        assertEquals("device-access", result.accessToken)
        assertTrue(updates.any { it.state == "waiting_for_user" && it.userCode == "ABCD-EFGH" })
        assertTrue(
            updates.any {
                it.state == "waiting_for_user" &&
                    it.verificationUrl == server.url("/codex/device").toString()
            },
        )
        assertEquals("/api/accounts/deviceauth/usercode", server.takeRequest().path)
        assertEquals("/api/accounts/deviceauth/token", server.takeRequest().path)
        assertEquals("/api/accounts/deviceauth/token", server.takeRequest().path)
        assertEquals("/oauth/token", server.takeRequest().path)
    }

    @Test
    fun deviceUserCode404KeepsDeviceAuthDisabledSemantics() {
        server.enqueue(MockResponse().setResponseCode(404))

        val error = deviceCodeFailure()

        assertEquals(OAuthFailureKind.DEVICE_AUTH_DISABLED, error.kind)
        assertEquals(404, error.statusCode)
        assertEquals("此账户未启用设备代码登录，请在 ChatGPT 安全设置中启用后重试", error.message)
    }

    @Test
    fun deviceUserCode403IsReportedAsOpenAiNetworkAccessFailure() {
        server.enqueue(MockResponse().setResponseCode(403))

        val error = deviceCodeFailure()

        assertEquals(OAuthFailureKind.NETWORK, error.kind)
        assertEquals(403, error.statusCode)
        assertEquals(OAuthException.NETWORK_ERROR_MESSAGE, error.message)
    }

    @Test
    fun deviceUserCodeIOExceptionUsesOpenAiNetworkMessage() {
        server.enqueue(MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_AT_START))

        val error = deviceCodeFailure()

        assertEquals(OAuthFailureKind.NETWORK, error.kind)
        assertEquals(null, error.statusCode)
        assertEquals(OAuthException.NETWORK_ERROR_MESSAGE, error.message)
    }

    @Test
    fun deviceUserCodeServerFailureDoesNotLookLikeDeviceAuthDisabled() {
        server.enqueue(MockResponse().setResponseCode(503))

        val error = deviceCodeFailure()

        assertEquals(OAuthFailureKind.SERVER, error.kind)
        assertEquals(503, error.statusCode)
        assertTrue(error.kind != OAuthFailureKind.DEVICE_AUTH_DISABLED)
    }

    private fun deviceCodeFailure(): OAuthException =
        (runCatching { client().login() }.exceptionOrNull() as? OAuthException)
            ?: error("Expected device-code OAuth failure")

    private fun client(): CodexOAuthClient = CodexOAuthClient(
        httpClient = OkHttpClient(),
        authBaseUrl = server.url("/").toString(),
    )

    private fun credentials() = OAuthCredentials(
        accessToken = "old-access",
        refreshToken = "old-refresh",
        idToken = "old-id",
    )
}
