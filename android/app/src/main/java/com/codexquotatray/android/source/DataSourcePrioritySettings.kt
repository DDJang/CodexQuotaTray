package com.codexquotatray.android.source

import android.content.Context

enum class DataSourcePriority {
    OPENAI_FIRST,
    WINDOWS_FIRST,
}

internal fun sourcePriorityChanged(
    lastObservedPriority: DataSourcePriority?,
    currentPriority: DataSourcePriority,
): Boolean = lastObservedPriority != null && lastObservedPriority != currentPriority

data class DataSourcePrioritySettings(
    val quota: DataSourcePriority = DataSourcePriority.OPENAI_FIRST,
    val token: DataSourcePriority = DataSourcePriority.WINDOWS_FIRST,
)

interface DataSourcePriorityStore {
    fun load(): DataSourcePrioritySettings
    fun save(value: DataSourcePrioritySettings): Boolean
}

class AndroidDataSourcePriorityStore(context: Context) : DataSourcePriorityStore {
    private val preferences = context.applicationContext.getSharedPreferences(
        "data_source_priority",
        Context.MODE_PRIVATE,
    )

    override fun load(): DataSourcePrioritySettings = DataSourcePrioritySettings(
        quota = read(KEY_QUOTA, DataSourcePriority.OPENAI_FIRST),
        token = read(KEY_TOKEN, DataSourcePriority.WINDOWS_FIRST),
    )

    override fun save(value: DataSourcePrioritySettings): Boolean = preferences.edit()
        .putString(KEY_QUOTA, value.quota.name)
        .putString(KEY_TOKEN, value.token.name)
        .commit()

    private fun read(key: String, fallback: DataSourcePriority): DataSourcePriority =
        preferences.getString(key, null)
            ?.let { stored -> DataSourcePriority.entries.firstOrNull { it.name == stored } }
            ?: fallback

    private companion object {
        const val KEY_QUOTA = "quota_priority"
        const val KEY_TOKEN = "token_priority"
    }
}
