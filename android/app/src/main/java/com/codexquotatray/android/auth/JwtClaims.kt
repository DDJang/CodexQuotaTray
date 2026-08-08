package com.codexquotatray.android.auth

import android.util.Base64
import org.json.JSONObject
import java.nio.charset.StandardCharsets

/** Reads only non-sensitive JWT claims needed for expiry, plan, and account routing. */
internal object JwtClaims {
    fun payload(token: String?): JSONObject? {
        if (token.isNullOrBlank()) return null
        val parts = token.split('.')
        if (parts.size < 2 || parts[1].isBlank()) return null
        return runCatching {
            val decoded = Base64.decode(parts[1], Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
            JSONObject(String(decoded, StandardCharsets.UTF_8))
        }.getOrNull()
    }

    fun accountId(token: String?): String? {
        val claims = payload(token) ?: return null
        val auth = claims.optJSONObject("https://api.openai.com/auth")
        return firstString(auth, "chatgpt_account_id")
            ?: firstString(claims, "chatgpt_account_id")
            ?: firstString(claims, "account_id")
    }

    fun planType(token: String?): String? {
        val claims = payload(token) ?: return null
        val auth = claims.optJSONObject("https://api.openai.com/auth")
        return firstString(auth, "chatgpt_plan_type")
            ?: firstString(claims, "chatgpt_plan_type")
    }

    fun expiresAtSeconds(token: String?): Long? {
        val claims = payload(token) ?: return null
        val value = claims.opt("exp")
        return when (value) {
            is Number -> value.toLong()
            is String -> value.trim().toLongOrNull()
            else -> null
        }
    }

    private fun firstString(json: JSONObject?, key: String): String? =
        json?.optString(key)?.trim()?.takeIf { it.isNotEmpty() && it != "null" }
}
