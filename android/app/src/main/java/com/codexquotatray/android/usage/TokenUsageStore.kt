package com.codexquotatray.android.usage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

interface TokenSyncPairingStore {
    fun load(): TokenSyncPairing?
    fun save(pairing: TokenSyncPairing): Boolean
}

internal fun interface TokenUsageCacheStore {
    fun save(snapshot: TokenUsageSnapshot): Boolean
}

class TokenSyncStore(context: Context) : TokenSyncPairingStore {
    private val preferences = context.applicationContext.getSharedPreferences("token_sync_pairing", Context.MODE_PRIVATE)

    override fun load(): TokenSyncPairing? = synchronized(lock) {
        preferences.getString(KEY_PAIRING, null)?.let(::decrypt)
    }

    override fun save(pairing: TokenSyncPairing): Boolean = synchronized(lock) {
        runCatching {
            val payload = JSONObject()
                .put("deviceId", pairing.deviceId)
                .put("host", pairing.lastKnownHost)
                .put("port", pairing.port)
                .put("secret", pairing.pairingSecret)
                .put("displayName", pairing.displayName ?: JSONObject.NULL)
                .put("lastSyncUtc", pairing.lastSyncUtc ?: JSONObject.NULL)
                .put("lastSuccessfulSyncAtMillis", pairing.lastSuccessfulSyncAtMillis ?: JSONObject.NULL)
                .toString().toByteArray(Charsets.UTF_8)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.ENCRYPT_MODE, key())
            val encrypted = cipher.iv + cipher.doFinal(payload)
            preferences.edit().putString(KEY_PAIRING, Base64.encodeToString(encrypted, Base64.NO_WRAP)).commit()
        }.getOrDefault(false)
    }

    private fun decrypt(encoded: String): TokenSyncPairing? = runCatching {
        val bytes = Base64.decode(encoded, Base64.DEFAULT)
        require(bytes.size > GCM_IV_BYTES)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(GCM_TAG_BITS, bytes.copyOfRange(0, GCM_IV_BYTES)))
        val json = JSONObject(String(cipher.doFinal(bytes.copyOfRange(GCM_IV_BYTES, bytes.size)), Charsets.UTF_8))
        TokenSyncEndpoint.validated(
            json.optString("deviceId", ""),
            json.getString("host"),
            json.getInt("port"),
            json.getString("secret"),
            (json.opt("displayName") as? String)?.takeIf { it.isNotBlank() },
            (json.opt("lastSyncUtc") as? String)?.takeIf { it.isNotBlank() },
            (json.opt("lastSuccessfulSyncAtMillis") as? Number)?.toLong(),
        )
    }.getOrNull()

    fun clear(): Boolean = synchronized(lock) {
        preferences.edit().remove(KEY_PAIRING).commit()
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (store.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE).run {
            init(KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build())
            generateKey()
        }
    }

    companion object {
        private val lock = Any()
        private const val KEY_PAIRING = "encrypted_pairing"
        private const val KEY_ALIAS = "codex_quota_token_sync_v1"
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val GCM_IV_BYTES = 12
        private const val GCM_TAG_BITS = 128
    }
}

class TokenUsageCache private constructor(private val file: File) : TokenUsageCacheStore {
    constructor(context: Context) : this(File(context.applicationContext.filesDir, "token-usage-cache.json"))

    fun load(): TokenUsageSnapshot? = runCatching {
        if (!file.isFile || file.length() > MAXIMUM_BYTES) null else TokenUsageJson.parse(file.readText(Charsets.UTF_8))
    }.getOrNull()

    override fun save(snapshot: TokenUsageSnapshot): Boolean = runCatching {
        file.parentFile?.mkdirs()
        val temporary = File(file.parentFile, file.name + ".tmp")
        val bytes = TokenUsageJson.serialize(snapshot).toByteArray(Charsets.UTF_8)
        require(bytes.size <= MAXIMUM_BYTES)
        temporary.outputStream().use { output ->
            output.write(bytes)
            output.flush()
            (output as java.io.FileOutputStream).fd.sync()
        }
        java.nio.file.Files.move(
            temporary.toPath(),
            file.toPath(),
            java.nio.file.StandardCopyOption.ATOMIC_MOVE,
            java.nio.file.StandardCopyOption.REPLACE_EXISTING,
        )
        true
    }.getOrDefault(false)

    companion object {
        private const val MAXIMUM_BYTES = 512 * 1024
        internal fun forTest(file: File) = TokenUsageCache(file)
    }
}
