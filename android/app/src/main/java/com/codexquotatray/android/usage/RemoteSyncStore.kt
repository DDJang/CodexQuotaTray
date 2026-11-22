package com.codexquotatray.android.usage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import org.json.JSONObject
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

internal data class RemoteSyncSettings(
    val connection: RemoteSyncConnection?,
    val lastSuccessAtMillis: Long?,
    val connectionId: String?,
) {
    fun cacheIdentity(machine: RemoteMachine): String? = connectionId?.toCacheIdentity(machine)
}

private fun String.toCacheIdentity(machine: RemoteMachine) = "$this:${machine.path}"

internal class RemoteSyncStore(context: Context) {
    private val preferences = context.getSharedPreferences("remote_sync", Context.MODE_PRIVATE)

    fun load(): RemoteSyncSettings = TokenUsagePairingLifecycle.withLock {
        val connection = loadConnection()
        val lastSuccess = if (connection == null) null else preferences.getLong(LAST_SUCCESS, 0).takeIf { it > 0 }
        val id = connection?.let {
            preferences.getString(CONNECTION_ID, null) ?: UUID.randomUUID().toString().also { newId ->
                preferences.edit().putString(CONNECTION_ID, newId).apply()
            }
        }
        RemoteSyncSettings(connection, lastSuccess, id)
    }

    private fun loadConnection(): RemoteSyncConnection? = runCatching {
        val encoded = preferences.getString(CONNECTION, null) ?: return null
        val bytes = Base64.decode(encoded, Base64.NO_WRAP)
        require(bytes.size > IV_BYTES)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(0, IV_BYTES)))
        val json = JSONObject(cipher.doFinal(bytes, IV_BYTES, bytes.size - IV_BYTES).toString(Charsets.UTF_8))
        RemoteSyncConnection.parse(json.getString("origin"), json.getString("readKey"))
    }.getOrNull()

    fun save(connection: RemoteSyncConnection): Boolean = TokenUsagePairingLifecycle.withLock {
        runCatching {
            val previous = loadConnection()
            val payload = JSONObject()
                .put("origin", connection.origin.toString())
                .put("readKey", connection.readKey)
                .toString().toByteArray(Charsets.UTF_8)
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.ENCRYPT_MODE, key())
            val encrypted = Base64.encodeToString(cipher.iv + cipher.doFinal(payload), Base64.NO_WRAP)
            val edit = preferences.edit().putString(CONNECTION, encrypted)
            if (previous?.origin != connection.origin || previous.readKey != connection.readKey) {
                edit.remove(LAST_SUCCESS).putString(CONNECTION_ID, UUID.randomUUID().toString())
            }
            edit.commit()
        }.getOrDefault(false)
    }

    fun clear(): Boolean = TokenUsagePairingLifecycle.withLock {
        preferences.edit().remove(CONNECTION).remove(CONNECTION_ID).remove(LAST_SUCCESS).commit()
    }

    fun commitIfCurrent(cacheIdentity: String, machine: RemoteMachine, commit: () -> Boolean): Boolean =
        TokenUsagePairingLifecycle.withLock {
            preferences.getString(CONNECTION_ID, null)?.toCacheIdentity(machine) == cacheIdentity && commit()
        }

    fun readSnapshot(
        client: RemoteSyncClient,
        connection: RemoteSyncConnection,
        machine: RemoteMachine,
    ): RemoteSyncSnapshot? = client.read(connection, machine).also { recordSuccess(connection) }

    private fun recordSuccess(connection: RemoteSyncConnection) = TokenUsagePairingLifecycle.withLock {
        val current = loadConnection()
        if (current?.origin == connection.origin && current.readKey == connection.readKey) {
            preferences.edit().putLong(LAST_SUCCESS, System.currentTimeMillis()).apply()
        }
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (store.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val CONNECTION = "encrypted_connection"
        const val CONNECTION_ID = "connection_id"
        const val LAST_SUCCESS = "last_connection_success"
        const val KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "codex_quota_remote_sync_v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_BYTES = 12
    }
}
