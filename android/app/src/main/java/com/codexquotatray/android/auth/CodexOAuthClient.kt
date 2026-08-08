package com.codexquotatray.android.auth

import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

data class OAuthLoginUpdate(
    val state: String,
    val userCode: String? = null,
    val verificationUrl: String? = null,
    val message: String? = null,
)

enum class OAuthFailureKind {
    DEVICE_AUTH_DISABLED,
    LOGIN_REQUIRED,
    REFRESH_EXPIRED,
    REFRESH_REVOKED,
    REFRESH_REUSED,
    INVALID_RESPONSE,
    NETWORK,
    SERVER,
}

class OAuthException(
    val kind: OAuthFailureKind,
    override val message: String,
    val statusCode: Int? = null,
) : IOException(message)

class CodexOAuthClient(
    private val httpClient: OkHttpClient = defaultClient(),
    private val authBaseUrl: String = OAuthCredentials.DEFAULT_AUTH_BASE_URL,
    private val clientId: String = OAuthCredentials.CLIENT_ID,
) {
    fun login(onUpdate: (OAuthLoginUpdate) -> Unit = {}): OAuthCredentials {
        onUpdate(OAuthLoginUpdate("login_starting"))
        val device = requestDeviceCode()
        onUpdate(
            OAuthLoginUpdate(
                state = "waiting_for_user",
                userCode = device.userCode,
                verificationUrl = device.verificationUrl,
            ),
        )
        val authorization = pollDeviceCode(device, onUpdate)
        onUpdate(OAuthLoginUpdate("exchanging_token"))
        return exchangeDeviceCode(authorization)
    }

    fun refresh(credentials: OAuthCredentials): OAuthCredentials {
        if (credentials.refreshToken.isBlank()) {
            throw OAuthException(OAuthFailureKind.LOGIN_REQUIRED, "refresh token unavailable")
        }
        val requestBody = JSONObject()
            .put("client_id", clientId)
            .put("grant_type", "refresh_token")
            .put("refresh_token", credentials.refreshToken)
            .put("scope", "openid profile email")
            .toString()
            .toRequestBody(JSON_MEDIA_TYPE)
        val response = execute(
            Request.Builder()
                .url(url("/oauth/token"))
                .post(requestBody)
                .header("Content-Type", "application/json")
                .build(),
        )
        if (response.code !in 200..299) throw refreshFailure(response)
        val json = parseObject(response.body, "refresh")
        val access = string(json, "access_token", "accessToken")
            ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "refresh response missing access token")
        val refresh = string(json, "refresh_token", "refreshToken") ?: credentials.refreshToken
        val idToken = string(json, "id_token", "idToken") ?: credentials.idToken
        return credentials.withTokens(access, refresh, idToken)
    }

    private fun requestDeviceCode(): DeviceCode {
        val body = JSONObject().put("client_id", clientId).toString().toRequestBody(JSON_MEDIA_TYPE)
        val response = execute(
            Request.Builder()
                .url(url("/api/accounts/deviceauth/usercode"))
                .post(body)
                .header("Content-Type", "application/json")
                .build(),
        )
        if (response.code == 404 || response.code == 403) {
            throw OAuthException(
                OAuthFailureKind.DEVICE_AUTH_DISABLED,
                "此账户未启用设备代码登录，请在 ChatGPT 安全设置中启用后重试",
                response.code,
            )
        }
        if (response.code !in 200..299) {
            throw OAuthException(OAuthFailureKind.SERVER, "device authorization request failed", response.code)
        }
        val json = parseObject(response.body, "device code")
        val deviceAuthId = string(json, "device_auth_id", "deviceAuthId")
            ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "device response missing id")
        val userCode = string(json, "user_code", "usercode", "userCode")
            ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "device response missing code")
        val interval = number(json, "interval")?.coerceIn(1L, 30L) ?: 5L
        return DeviceCode(
            deviceAuthId,
            userCode,
            interval,
            authBaseUrl.trimEnd('/') + "/codex/device",
        )
    }

    private fun pollDeviceCode(
        device: DeviceCode,
        onUpdate: (OAuthLoginUpdate) -> Unit,
    ): DeviceAuthorization {
        val deadline = System.currentTimeMillis() + TimeUnit.MINUTES.toMillis(15)
        while (System.currentTimeMillis() < deadline) {
            val body = JSONObject()
                .put("device_auth_id", device.deviceAuthId)
                .put("user_code", device.userCode)
                .toString()
                .toRequestBody(JSON_MEDIA_TYPE)
            val response = execute(
                Request.Builder()
                    .url(url("/api/accounts/deviceauth/token"))
                    .post(body)
                    .header("Content-Type", "application/json")
                    .build(),
            )
            if (response.code in 200..299) {
                val json = parseObject(response.body, "device token")
                val authorizationCode = string(json, "authorization_code", "authorizationCode")
                    ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "device token missing authorization code")
                val challenge = string(json, "code_challenge", "codeChallenge")
                    ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "device token missing code challenge")
                val verifier = string(json, "code_verifier", "codeVerifier")
                    ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "device token missing code verifier")
                return DeviceAuthorization(authorizationCode, challenge, verifier)
            }
            if (response.code != 403 && response.code != 404) {
                throw OAuthException(OAuthFailureKind.SERVER, "device authorization polling failed", response.code)
            }
            onUpdate(OAuthLoginUpdate("waiting_for_user", device.userCode, device.verificationUrl))
            Thread.sleep(TimeUnit.SECONDS.toMillis(device.intervalSeconds))
        }
        throw OAuthException(OAuthFailureKind.LOGIN_REQUIRED, "device authorization timed out")
    }

    private fun exchangeDeviceCode(authorization: DeviceAuthorization): OAuthCredentials {
        val form = FormBody.Builder()
            .add("grant_type", "authorization_code")
            .add("code", authorization.authorizationCode)
            .add("redirect_uri", url("/deviceauth/callback"))
            .add("client_id", clientId)
            .add("code_verifier", authorization.codeVerifier)
            .build()
        val response = execute(
            Request.Builder()
                .url(url("/oauth/token"))
                .post(form)
                .build(),
        )
        if (response.code !in 200..299) {
            throw OAuthException(OAuthFailureKind.SERVER, "OAuth token exchange failed", response.code)
        }
        val json = parseObject(response.body, "OAuth token")
        val access = string(json, "access_token", "accessToken")
            ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "OAuth response missing access token")
        val refresh = string(json, "refresh_token", "refreshToken")
            ?: throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "OAuth response missing refresh token")
        val idToken = string(json, "id_token", "idToken")
        return OAuthCredentials(
            accessToken = access,
            refreshToken = refresh,
            idToken = idToken,
            accountId = JwtClaims.accountId(idToken) ?: JwtClaims.accountId(access),
            accessTokenExpiresAtSeconds = JwtClaims.expiresAtSeconds(access)
                ?: JwtClaims.expiresAtSeconds(idToken),
            lastRefreshMillis = System.currentTimeMillis(),
        )
    }

    private fun refreshFailure(response: HttpPayload): OAuthException {
        val code = runCatching { JSONObject(response.body).opt("error") }.getOrNull()
            ?.let { if (it is JSONObject) it.optString("code") else it.toString() }
            ?.lowercase()
        val kind = when (code) {
            "refresh_token_expired" -> OAuthFailureKind.REFRESH_EXPIRED
            "refresh_token_reused" -> OAuthFailureKind.REFRESH_REUSED
            "invalid_grant", "refresh_token_invalidated" -> OAuthFailureKind.REFRESH_REVOKED
            else -> if (response.code == 401) OAuthFailureKind.REFRESH_EXPIRED else OAuthFailureKind.SERVER
        }
        return OAuthException(kind, "OAuth token refresh failed", response.code)
    }

    private fun execute(request: Request): HttpPayload = try {
        httpClient.newCall(request).execute().use { response ->
            HttpPayload(response.code, response.body?.string().orEmpty())
        }
    } catch (error: IOException) {
        throw OAuthException(OAuthFailureKind.NETWORK, "OAuth network request failed")
    }

    private fun parseObject(raw: String, operation: String): JSONObject =
        runCatching { JSONObject(raw) }.getOrElse {
            throw OAuthException(OAuthFailureKind.INVALID_RESPONSE, "$operation response was not JSON")
        }

    private fun string(json: JSONObject, vararg keys: String): String? = keys.asSequence()
        .mapNotNull { key -> json.opt(key).takeIf { it is String } as String? }
        .map(String::trim)
        .firstOrNull(String::isNotEmpty)

    private fun number(json: JSONObject, key: String): Long? = when (val value = json.opt(key)) {
        is Number -> value.toLong()
        is String -> value.trim().toLongOrNull()
        else -> null
    }

    private fun url(path: String): String = authBaseUrl.trimEnd('/') + "/" + path.trimStart('/')

    private data class DeviceCode(
        val deviceAuthId: String,
        val userCode: String,
        val intervalSeconds: Long,
        val verificationUrl: String,
    )

    private data class DeviceAuthorization(
        val authorizationCode: String,
        val codeChallenge: String,
        val codeVerifier: String,
    )

    private data class HttpPayload(val code: Int, val body: String)

    companion object {
        private val JSON_MEDIA_TYPE = "application/json; charset=utf-8".toMediaType()

        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .callTimeout(45, TimeUnit.SECONDS)
            .build()
    }
}
