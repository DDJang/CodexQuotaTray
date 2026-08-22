package com.codexquotatray.android.auth

import java.util.concurrent.TimeUnit

data class OAuthCredentials(
    val accessToken: String,
    val refreshToken: String,
    val idToken: String? = null,
    val accountId: String? = null,
    val accessTokenExpiresAtSeconds: Long? = null,
    val lastRefreshMillis: Long? = null,
) {
    fun needsRefresh(nowMillis: Long = System.currentTimeMillis()): Boolean {
        val expiresAt = accessTokenExpiresAtSeconds
        if (expiresAt != null) {
            return expiresAt * 1_000L <= nowMillis + REFRESH_SKEW_MILLIS
        }
        val lastRefresh = lastRefreshMillis ?: return false
        return nowMillis - lastRefresh >= REFRESH_AFTER_MILLIS
    }

    fun withTokens(
        accessToken: String,
        refreshToken: String,
        idToken: String? = this.idToken,
        nowMillis: Long = System.currentTimeMillis(),
    ): OAuthCredentials {
        val account = JwtClaims.accountId(idToken)
            ?: JwtClaims.accountId(accessToken)
            ?: accountId
        return copy(
            accessToken = accessToken,
            refreshToken = refreshToken,
            idToken = idToken,
            accountId = account,
            accessTokenExpiresAtSeconds = JwtClaims.expiresAtSeconds(accessToken)
                ?: JwtClaims.expiresAtSeconds(idToken),
            lastRefreshMillis = nowMillis,
        )
    }

    companion object {
        const val CLIENT_ID = "app_EMoamEEZ73f0CkXaXp7hrann"
        const val DEFAULT_AUTH_BASE_URL = "https://auth.openai.com"
        const val REFRESH_TOKEN_URL = "$DEFAULT_AUTH_BASE_URL/oauth/token"
        const val USAGE_URL = "https://chatgpt.com/backend-api/wham/usage"
        const val RESET_CREDITS_URL = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"
        const val DEVICE_CODE_URL = "$DEFAULT_AUTH_BASE_URL/api/accounts/deviceauth/usercode"
        const val DEVICE_TOKEN_URL = "$DEFAULT_AUTH_BASE_URL/api/accounts/deviceauth/token"
        const val DEVICE_VERIFICATION_URL = "$DEFAULT_AUTH_BASE_URL/codex/device"
        const val DEVICE_REDIRECT_URI = "$DEFAULT_AUTH_BASE_URL/deviceauth/callback"

        private val REFRESH_SKEW_MILLIS = TimeUnit.MINUTES.toMillis(5)
        private val REFRESH_AFTER_MILLIS = TimeUnit.DAYS.toMillis(8)
    }
}
