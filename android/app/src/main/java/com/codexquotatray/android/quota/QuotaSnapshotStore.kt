package com.codexquotatray.android.quota

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.ResetCreditSnapshot
import com.codexquotatray.android.usage.TokenUsagePairingLifecycle

/**
 * Stores only the last successful quota response, never credentials or raw HTTP data.
 * It lets the foreground page display the result produced by WorkManager.
 */
class QuotaSnapshotStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun save(
        result: DirectQuotaResult,
        lastSuccessfulRefreshAtMillis: Long = System.currentTimeMillis(),
        windowsDeviceIdentity: String? = null,
    ): Unit = TokenUsagePairingLifecycle.withLock {
        val root = JSONObject()
            .putNullable("planType", result.planType)
            .put("quotaState", result.quotaState)
            .put("updatedAtMillis", result.updatedAtMillis)
            .put("lastSuccessfulRefreshAtMillis", lastSuccessfulRefreshAtMillis)
            .put("source", result.source.name)
            .putNullable(
                "windowsDeviceIdentity",
                result.source.takeIf { it == QuotaSource.WINDOWS }?.let { windowsDeviceIdentity },
            )
            .put(
                "windows",
                JSONArray().apply {
                    result.windows.forEach { window ->
                        put(
                            JSONObject()
                                .putNullable("limitId", window.limitId)
                                .putNullable("limitName", window.limitName)
                                .putNullable("planType", window.planType)
                                .put("sourceSlot", window.sourceSlot)
                                .putNullable("usedPercent", window.usedPercent)
                                .putNullable("remainingPercent", window.remainingPercent)
                                .putNullable("windowDurationMins", window.windowDurationMins)
                                .putNullable("resetsAt", window.resetsAt)
                                .putNullable("bucketId", window.bucketId),
                        )
                    }
                },
            )
            .put(
                "resetCredits",
                result.resetCredits?.let(QuotaSnapshotResetCreditsCodec::encode) ?: JSONObject.NULL,
            )
        preferences.edit()
            .putString(KEY_SNAPSHOT, root.toString())
            .putLong(KEY_LAST_SUCCESSFUL_REFRESH_AT_MILLIS, lastSuccessfulRefreshAtMillis)
            .commit()
        Unit
    }

    fun load(currentWindowsDeviceIdentity: String? = null): DirectQuotaResult? =
        TokenUsagePairingLifecycle.withLock {
            val raw = preferences.getString(KEY_SNAPSHOT, null) ?: return@withLock null
            runCatching {
                val root = JSONObject(raw)
                val source = root.stringOrNull("source")
                    ?.let { runCatching { QuotaSource.valueOf(it) }.getOrNull() }
                    ?: QuotaSource.DIRECT
                if (!shouldRestoreQuotaSnapshot(
                        source,
                        root.stringOrNull("windowsDeviceIdentity"),
                        currentWindowsDeviceIdentity,
                    )) {
                    return@runCatching null
                }
                val windowsJson = root.optJSONArray("windows") ?: JSONArray()
                val windows = buildList {
                    for (index in 0 until windowsJson.length()) {
                        val window = windowsJson.optJSONObject(index) ?: continue
                        add(
                            QuotaWindow(
                                limitId = window.stringOrNull("limitId"),
                                limitName = window.stringOrNull("limitName"),
                                planType = window.stringOrNull("planType"),
                                sourceSlot = window.stringOrNull("sourceSlot").orEmpty(),
                                usedPercent = window.intOrNull("usedPercent"),
                                remainingPercent = window.intOrNull("remainingPercent"),
                                windowDurationMins = window.longOrNull("windowDurationMins"),
                                resetsAt = window.longOrNull("resetsAt"),
                                bucketId = window.stringOrNull("bucketId"),
                            ),
                        )
                    }
                }
                val resetCredits = QuotaSnapshotResetCreditsCodec.decode(root)
                DirectQuotaResult(
                    planType = root.stringOrNull("planType"),
                    windows = windows,
                    quotaState = root.stringOrNull("quotaState") ?: "unavailable",
                    updatedAtMillis = root.longOrNull("updatedAtMillis") ?: return@runCatching null,
                    source = source,
                    resetCredits = resetCredits,
                )
            }.getOrNull()
        }

    /** Invalidates only a Windows snapshot that belongs to another device. */
    fun invalidateWindowsForPairing(currentWindowsDeviceIdentity: String?) =
        TokenUsagePairingLifecycle.withLock {
            val raw = preferences.getString(KEY_SNAPSHOT, null) ?: return@withLock
            val isWindowsSnapshot = runCatching {
                JSONObject(raw).stringOrNull("source") == QuotaSource.WINDOWS.name
            }.getOrDefault(false)
            if (!isWindowsSnapshot) return@withLock
            val storedIdentity = runCatching {
                JSONObject(raw).stringOrNull("windowsDeviceIdentity")
            }.getOrNull()
            if (shouldInvalidateWindowsQuotaSnapshot(
                    QuotaSource.WINDOWS,
                    storedIdentity,
                    currentWindowsDeviceIdentity,
                )) {
                clearSnapshot(synchronous = true)
            }
        }

    fun clear() = TokenUsagePairingLifecycle.withLock { clearSnapshot() }

    private fun clearSnapshot(synchronous: Boolean = false) {
        val editor = preferences.edit()
            .remove(KEY_SNAPSHOT)
            .remove(KEY_LAST_SUCCESSFUL_REFRESH_AT_MILLIS)
        if (synchronous) editor.commit() else editor.apply()
    }

    /**
     * Historical completion timestamp retained for cache compatibility. The
     * foreground automatic gate now uses its process-local last-attempt time.
     */
    fun lastSuccessfulRefreshAtMillis(): Long? = preferences
        .getLong(KEY_LAST_SUCCESSFUL_REFRESH_AT_MILLIS, 0L)
        .takeIf { it > 0L }

    companion object {
        private const val PREFERENCES_NAME = "quota_snapshot"
        private const val KEY_SNAPSHOT = "last_successful_snapshot"
        private const val KEY_LAST_SUCCESSFUL_REFRESH_AT_MILLIS = "last_successful_refresh_at_millis"
    }
}

internal fun windowsQuotaSnapshotMatchesPairing(
    storedDeviceIdentity: String?,
    currentDeviceIdentity: String?,
): Boolean = !storedDeviceIdentity.isNullOrBlank() &&
    !currentDeviceIdentity.isNullOrBlank() &&
    storedDeviceIdentity.equals(currentDeviceIdentity, ignoreCase = true)

internal fun shouldRestoreQuotaSnapshot(
    source: QuotaSource,
    storedWindowsDeviceIdentity: String?,
    currentWindowsDeviceIdentity: String?,
): Boolean = source != QuotaSource.WINDOWS ||
    windowsQuotaSnapshotMatchesPairing(storedWindowsDeviceIdentity, currentWindowsDeviceIdentity)

internal fun shouldInvalidateWindowsQuotaSnapshot(
    source: QuotaSource,
    storedWindowsDeviceIdentity: String?,
    currentWindowsDeviceIdentity: String?,
): Boolean = source == QuotaSource.WINDOWS &&
    !windowsQuotaSnapshotMatchesPairing(storedWindowsDeviceIdentity, currentWindowsDeviceIdentity)

private fun JSONObject.putNullable(key: String, value: Any?): JSONObject =
    put(key, value ?: JSONObject.NULL)

private fun JSONObject.stringOrNull(key: String): String? =
    opt(key).takeIf { it is String } as String?

private fun JSONObject.intOrNull(key: String): Int? = when (val value = opt(key)) {
    is Number -> value.toInt()
    is String -> value.toIntOrNull()
    else -> null
}

private fun JSONObject.longOrNull(key: String): Long? = when (val value = opt(key)) {
    is Number -> value.toLong()
    is String -> value.toLongOrNull()
    else -> null
}

/** Shared by the store and pure JVM tests so null and [] remain distinct. */
internal object QuotaSnapshotResetCreditsCodec {
    fun encode(snapshot: ResetCreditSnapshot): JSONObject = JSONObject()
        .putNullable("availableCount", snapshot.availableCount)
        .put(
            "credits",
            snapshot.credits?.let { credits ->
                JSONArray().apply {
                    credits.forEach { credit ->
                        put(
                            JSONObject()
                                // Full reset-credit IDs are intentionally not persisted.
                                .putNullable("id", null)
                                .putNullable("resetType", credit.resetType)
                                .putNullable("status", credit.status)
                                .putNullable("grantedAt", credit.grantedAt)
                                .putNullable("expiresAt", credit.expiresAt)
                                .putNullable("title", credit.title)
                                .putNullable("description", credit.description),
                        )
                    }
                }
            } ?: JSONObject.NULL,
        )

    fun decode(root: JSONObject): ResetCreditSnapshot? {
        if (!root.has("resetCredits")) return null
        val value = root.opt("resetCredits")
        if (value === JSONObject.NULL) return null
        val summary = value as? JSONObject ?: return null
        val credits: List<ResetCredit>? = if (
            !summary.has("credits") || summary.opt("credits") === JSONObject.NULL
        ) {
            null
        } else {
            val array = summary.optJSONArray("credits") ?: return null
            buildList<ResetCredit> {
                for (index in 0 until array.length()) {
                    val credit = array.optJSONObject(index) ?: continue
                    add(
                        ResetCredit(
                                id = null,
                            resetType = credit.stringOrNull("resetType"),
                            status = credit.stringOrNull("status"),
                            grantedAt = credit.longOrNull("grantedAt"),
                            expiresAt = credit.longOrNull("expiresAt"),
                            title = credit.stringOrNull("title"),
                            description = credit.stringOrNull("description"),
                        ),
                    )
                }
            }
        }
        return ResetCreditSnapshot(
            availableCount = summary.longOrNull("availableCount"),
            credits = credits,
        )
    }
}
