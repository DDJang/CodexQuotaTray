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
    fun clear(): Boolean = false

    /** Best-effort LAN status projection; failures never affect the network result. */
    fun recordLanSuccess(expected: TokenSyncPairing, attempt: LanAttemptContext): Boolean = false

    fun recordLanFailure(expected: TokenSyncPairing, attempt: LanAttemptContext): Boolean = false

    /** Atomically persist only when the user has not replaced this pairing. */
    fun saveIfCurrent(expected: TokenSyncPairing, updated: TokenSyncPairing): Boolean =
        load()?.takeIf { it.matchesConfiguration(expected) }?.let { save(updated) } ?: false
}

internal interface TokenUsageCacheStore {
    fun save(pairing: TokenSyncPairing, snapshot: TokenUsageSnapshot): Boolean
    fun saveOpenAI(snapshot: TokenUsageSnapshot): Boolean = false
    fun clear(): Boolean
}

/** Shared by pairing changes and the final sync commit boundary. */
internal object TokenUsagePairingLifecycle {
    private val lock = Any()

    fun <T> withLock(block: () -> T): T = synchronized(lock, block)

    fun clear(pairingStore: TokenSyncPairingStore, cache: TokenUsageCacheStore): Boolean = withLock {
        runCatching { cache.clear() }
        pairingStore.clear()
    }
}

class TokenSyncStore(context: Context) : TokenSyncPairingStore {
    private val preferences = context.applicationContext.getSharedPreferences("token_sync_pairing", Context.MODE_PRIVATE)

    override fun load(): TokenSyncPairing? = TokenUsagePairingLifecycle.withLock {
        preferences.getString(KEY_PAIRING, null)?.let(::decrypt)
    }

    override fun save(pairing: TokenSyncPairing): Boolean = TokenUsagePairingLifecycle.withLock {
        write(pairing)
    }

    override fun saveIfCurrent(expected: TokenSyncPairing, updated: TokenSyncPairing): Boolean = TokenUsagePairingLifecycle.withLock {
        load()?.takeIf { it.matchesConfiguration(expected) }?.let { write(updated) } ?: false
    }

    private fun write(pairing: TokenSyncPairing): Boolean {
        return runCatching {
            val payload = JSONObject()
                .put("deviceId", pairing.deviceId)
                .put("host", pairing.lastKnownHost)
                .put("port", pairing.port)
                .put("secret", pairing.pairingSecret)
                .put("displayName", pairing.displayName ?: JSONObject.NULL)
                .put("lastSyncUtc", pairing.lastSyncUtc ?: JSONObject.NULL)
                .put("lastSuccessfulSyncAtMillis", pairing.lastSuccessfulSyncAtMillis ?: JSONObject.NULL)
                .put("lastLanSuccessAtMillis", pairing.lastLanSuccessAtMillis ?: JSONObject.NULL)
                .put("lastLanFailureAtMillis", pairing.lastLanFailureAtMillis ?: JSONObject.NULL)
                .put("lastLanFailurePhase", pairing.lastLanFailurePhase ?: JSONObject.NULL)
                .put("lastLanAttemptId", pairing.lastLanAttemptId ?: JSONObject.NULL)
                .put("lastLanAttemptChannel", pairing.lastLanAttemptChannel ?: JSONObject.NULL)
                .put("lastLanTargetEndpoint", pairing.lastLanTargetEndpoint ?: JSONObject.NULL)
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
            (json.opt("lastLanSuccessAtMillis") as? Number)?.toLong(),
            (json.opt("lastLanFailureAtMillis") as? Number)?.toLong(),
            (json.opt("lastLanFailurePhase") as? String)?.takeIf { it.isNotBlank() },
            (json.opt("lastLanAttemptId") as? Number)?.toLong(),
            (json.opt("lastLanAttemptChannel") as? String)?.takeIf { it.isNotBlank() },
            (json.opt("lastLanTargetEndpoint") as? String)?.takeIf { it.isNotBlank() },
        )
    }.getOrNull()

    override fun clear(): Boolean = TokenUsagePairingLifecycle.withLock {
        preferences.edit().remove(KEY_PAIRING).commit()
    }

    override fun recordLanSuccess(expected: TokenSyncPairing, attempt: LanAttemptContext): Boolean =
        if (attempt.isStale()) false else
            updateLanState(expected) { pairing -> TokenSyncEndpoint.markLanSuccess(pairing, attempt) }

    override fun recordLanFailure(expected: TokenSyncPairing, attempt: LanAttemptContext): Boolean =
        if (attempt.isStale()) false else
            updateLanState(expected) { pairing -> TokenSyncEndpoint.markLanFailure(pairing, attempt) }

    private fun updateLanState(
        expected: TokenSyncPairing,
        update: (TokenSyncPairing) -> TokenSyncPairing,
    ): Boolean = TokenUsagePairingLifecycle.withLock {
        load()?.takeIf { it.matchesConfiguration(expected) }?.let { write(update(it)) } ?: false
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

    fun load(pairing: TokenSyncPairing): TokenUsageSnapshot? = runCatching {
        if (!file.isFile || file.length() > MAXIMUM_BYTES) null else {
            val root = JSONObject(file.readText(Charsets.UTF_8))
            if (root.optString("pairingIdentity") != pairing.cacheIdentity()) null
            else root.optJSONObject("snapshot")?.let { TokenUsageJson.parse(it.toString()) }
        }
    }.getOrNull()

    fun loadForAvailableSources(
        pairing: TokenSyncPairing?,
        hasOAuth: Boolean,
    ): TokenUsageSnapshot? = runCatching {
        if (!file.isFile || file.length() > MAXIMUM_BYTES) null else {
            val root = JSONObject(file.readText(Charsets.UTF_8))
            val snapshot = root.optJSONObject("snapshot")?.let { TokenUsageJson.parse(it.toString()) }
            when (snapshot?.transport) {
                DataTransport.WINDOWS -> snapshot.takeIf {
                    pairing != null && root.optString("pairingIdentity") == pairing.cacheIdentity()
                }
                DataTransport.OPENAI -> snapshot.takeIf {
                    hasOAuth && root.optString("pairingIdentity") == OPENAI_CACHE_IDENTITY
                }
                null -> null
            }
        }
    }.getOrNull()

    override fun save(pairing: TokenSyncPairing, snapshot: TokenUsageSnapshot): Boolean = runCatching {
        write(pairing.cacheIdentity(), snapshot.copy(transport = DataTransport.WINDOWS))
    }.getOrDefault(false)

    override fun saveOpenAI(snapshot: TokenUsageSnapshot): Boolean = runCatching {
        write(
            OPENAI_CACHE_IDENTITY,
            snapshot.copy(transport = DataTransport.OPENAI, scope = TokenUsageScope.ACCOUNT),
        )
    }.getOrDefault(false)

    private fun write(identity: String, snapshot: TokenUsageSnapshot): Boolean {
        file.parentFile?.mkdirs()
        val temporary = File(file.parentFile, file.name + ".tmp")
        val bytes = JSONObject()
            .put("pairingIdentity", identity)
            .put("snapshot", JSONObject(TokenUsageJson.serialize(snapshot)))
            .toString()
            .toByteArray(Charsets.UTF_8)
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
        return true
    }

    override fun clear(): Boolean = !file.exists() || file.delete()

    companion object {
        private const val MAXIMUM_BYTES = 512 * 1024
        private const val OPENAI_CACHE_IDENTITY = "openai-account"
        internal fun forTest(file: File) = TokenUsageCache(file)
    }
}
