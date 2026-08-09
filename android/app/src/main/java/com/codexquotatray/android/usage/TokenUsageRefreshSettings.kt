package com.codexquotatray.android.usage

import android.content.Context

data class TokenUsageRefreshSettings(
    val autoSyncOnOpen: Boolean = true,
)

class TokenUsageRefreshSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): TokenUsageRefreshSettings = TokenUsageRefreshSettings(
        autoSyncOnOpen = preferences.getBoolean(KEY_AUTO_SYNC_ON_OPEN, true),
    )

    fun save(settings: TokenUsageRefreshSettings) {
        preferences.edit()
            .putBoolean(KEY_AUTO_SYNC_ON_OPEN, settings.autoSyncOnOpen)
            .apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "token_usage_refresh_settings"
        private const val KEY_AUTO_SYNC_ON_OPEN = "auto_sync_on_open"
    }
}
