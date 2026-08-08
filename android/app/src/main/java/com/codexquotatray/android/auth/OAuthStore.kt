package com.codexquotatray.android.auth

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import java.time.Instant
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class OAuthStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )
    private val legacyAuthFile = File(
        context.applicationContext.filesDir,
        "codex-home/.codex/auth.json",
    )

    @Synchronized
    fun load(): OAuthCredentials? {
        val encrypted = preferences.getString(KEY_ENCRYPTED_CREDENTIALS, null)
        if (!encrypted.isNullOrBlank()) {
            decrypt(encrypted)?.let { return it }
        }

        val migrated = legacyAuthFile.takeIf(File::isFile)
            ?.let { file -> runCatching { AuthJsonParser.parse(file.readText()) }.getOrNull() }
            ?: return null
        save(migrated)
        return migrated
    }

    @Synchronized
    fun save(credentials: OAuthCredentials): Boolean = runCatching {
        val payload = JSONObject()
            .put(
                "tokens",
                JSONObject()
                    .put("access_token", credentials.accessToken)
                    .put("refresh_token", credentials.refreshToken)
                    .put("id_token", credentials.idToken)
                    .put("account_id", credentials.accountId)
                    .put("access_token_expires_at", credentials.accessTokenExpiresAtSeconds),
            )
            .put("last_refresh", credentials.lastRefreshMillis)
            .toString()
            .toByteArray(Charsets.UTF_8)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val ciphertext = cipher.iv + cipher.doFinal(payload)
        preferences.edit()
            .putString(
                KEY_ENCRYPTED_CREDENTIALS,
                Base64.encodeToString(ciphertext, Base64.NO_WRAP),
            )
            .remove(KEY_ACCESS_TOKEN)
            .remove(KEY_REFRESH_TOKEN)
            .remove(KEY_ID_TOKEN)
            .remove(KEY_ACCOUNT_ID)
            .remove(KEY_ACCESS_TOKEN_EXPIRES_AT)
            .remove(KEY_LAST_REFRESH)
            .commit()
    }.getOrDefault(false)

    @Synchronized
    fun clear() {
        preferences.edit().clear().commit()
    }

    private fun decrypt(encoded: String): OAuthCredentials? = runCatching {
        val ciphertext = Base64.decode(encoded, Base64.DEFAULT)
        require(ciphertext.size > GCM_IV_BYTES)
        val iv = ciphertext.copyOfRange(0, GCM_IV_BYTES)
        val encryptedPayload = ciphertext.copyOfRange(GCM_IV_BYTES, ciphertext.size)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(GCM_TAG_BITS, iv))
        AuthJsonParser.parse(String(cipher.doFinal(encryptedPayload), Charsets.UTF_8))
    }.getOrNull()

    private fun key(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        val existing = keyStore.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build(),
        )
        return generator.generateKey()
    }

    companion object {
        private const val PREFERENCES_NAME = "oauth_credentials"
        private const val KEY_ENCRYPTED_CREDENTIALS = "encrypted_credentials"
        private const val KEY_ACCESS_TOKEN = "access_token"
        private const val KEY_REFRESH_TOKEN = "refresh_token"
        private const val KEY_ID_TOKEN = "id_token"
        private const val KEY_ACCOUNT_ID = "account_id"
        private const val KEY_ACCESS_TOKEN_EXPIRES_AT = "access_token_expires_at"
        private const val KEY_LAST_REFRESH = "last_refresh"
        private const val KEY_ALIAS = "codex_quota_oauth_v1"
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val GCM_IV_BYTES = 12
        private const val GCM_TAG_BITS = 128
    }
}

internal object AuthJsonParser {
    fun parse(raw: String): OAuthCredentials? {
        val root = runCatching { JSONObject(raw) }.getOrNull() ?: return null
        val tokens = root.optJSONObject("tokens") ?: return null
        val access = string(tokens, "access_token", "accessToken") ?: return null
        val refresh = string(tokens, "refresh_token", "refreshToken").orEmpty()
        val idToken = string(tokens, "id_token", "idToken")
        val accountId = string(tokens, "account_id", "accountId") ?: JwtClaims.accountId(idToken)
        val expires = number(tokens, "access_token_expires_at", "accessTokenExpiresAt")
            ?: JwtClaims.expiresAtSeconds(access)
            ?: JwtClaims.expiresAtSeconds(idToken)
        return OAuthCredentials(
            accessToken = access,
            refreshToken = refresh,
            idToken = idToken,
            accountId = accountId,
            accessTokenExpiresAtSeconds = expires,
            lastRefreshMillis = parseTime(root.opt("last_refresh") ?: root.opt("lastRefresh")),
        )
    }

    private fun string(json: JSONObject, vararg keys: String): String? = keys.asSequence()
        .mapNotNull { key -> json.opt(key).takeIf { it is String } as String? }
        .map(String::trim)
        .firstOrNull(String::isNotEmpty)

    private fun number(json: JSONObject, vararg keys: String): Long? = keys.asSequence()
        .mapNotNull { key ->
            when (val value = json.opt(key)) {
                is Number -> value.toLong()
                is String -> value.trim().toLongOrNull()
                else -> null
            }
        }
        .firstOrNull()

    private fun parseTime(value: Any?): Long? = when (value) {
        is Number -> value.toLong().let { if (it < 10_000_000_000L) it * 1_000L else it }
        is String -> runCatching { Instant.parse(value).toEpochMilli() }.getOrNull()
        else -> null
    }
}
