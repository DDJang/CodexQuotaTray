package com.codexquotatray.android.auth

import java.security.MessageDigest
import java.util.Locale

enum class OAuthRefreshReason { PROACTIVE, UNAUTHORIZED_RECOVERY }

internal fun safeRefreshErrorCode(code: String?): String = when (val normalized = code?.lowercase(Locale.ROOT)) {
    null, "" -> "none"
    "refresh_token_expired", "refresh_token_reused", "refresh_token_invalidated", "invalid_grant" -> normalized
    else -> "unknown_" + MessageDigest.getInstance("SHA-256")
        .digest(normalized.toByteArray(Charsets.UTF_8)).take(6).joinToString("") { "%02x".format(it) }
}

internal class CredentialPersistenceException :
    OAuthException(OAuthFailureKind.SERVER, "刷新后的认证信息无法保存到本机，请重试")

/** Used under OAuthStoreLock, shared by every store in this application. */
internal class PendingCredentialWrite {
    private var pending: OAuthCredentials? = null
    val hasPending: Boolean get() = pending != null

    fun clear() { pending = null }

    fun save(
        value: OAuthCredentials,
        persist: (OAuthCredentials) -> Boolean,
        clearStale: () -> Boolean,
        log: (String) -> Unit,
    ) {
        pending = value
        retry(persist, clearStale, log)
    }

    fun retry(
        persist: (OAuthCredentials) -> Boolean,
        clearStale: () -> Boolean,
        log: (String) -> Unit,
    ): OAuthCredentials? {
        val value = pending ?: return null
        val saved = runCatching { persist(value) }.getOrDefault(false)
        runCatching { log("OAuth refresh persisted=$saved") }
        if (!saved) {
            val cleared = runCatching(clearStale).getOrDefault(false)
            runCatching { log("OAuth refresh stale_storage_cleared=$cleared") }
            throw CredentialPersistenceException()
        }
        pending = null
        return value
    }
}
