package com.codexquotatray.android.auth

import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
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
        assertEquals("openid profile email", body.getString("scope"))
        assertEquals("new-access", result.accessToken)
        assertEquals("new-refresh", result.refreshToken)
        assertEquals("new-id", result.idToken)
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
