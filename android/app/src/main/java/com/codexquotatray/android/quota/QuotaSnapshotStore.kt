package com.codexquotatray.android.quota

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.QuotaWindow

/**
 * Stores only the last successful quota response, never credentials or raw HTTP data.
 * It lets the foreground page display the result produced by WorkManager.
 */
class QuotaSnapshotStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun save(result: DirectQuotaResult) {
        val root = JSONObject()
            .putNullable("planType", result.planType)
            .put("quotaState", result.quotaState)
            .put("updatedAtMillis", result.updatedAtMillis)
            .put("source", result.source.name)
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
                                .putNullable("resetsAt", window.resetsAt),
                        )
                    }
                },
            )
        preferences.edit().putString(KEY_SNAPSHOT, root.toString()).commit()
    }

    fun load(): DirectQuotaResult? {
        val raw = preferences.getString(KEY_SNAPSHOT, null) ?: return null
        return runCatching {
            val root = JSONObject(raw)
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
                        ),
                    )
                }
            }
            DirectQuotaResult(
                planType = root.stringOrNull("planType"),
                windows = windows,
                quotaState = root.stringOrNull("quotaState") ?: "unavailable",
                updatedAtMillis = root.longOrNull("updatedAtMillis") ?: return null,
                source = root.stringOrNull("source")
                    ?.let { runCatching { QuotaSource.valueOf(it) }.getOrNull() }
                    ?: QuotaSource.DIRECT,
            )
        }.getOrNull()
    }

    fun clear() {
        preferences.edit().remove(KEY_SNAPSHOT).apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "quota_snapshot"
        private const val KEY_SNAPSHOT = "last_successful_snapshot"
    }
}

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
