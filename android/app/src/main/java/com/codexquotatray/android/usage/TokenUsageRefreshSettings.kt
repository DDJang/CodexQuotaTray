package com.codexquotatray.android.usage

import android.content.Context

data class TokenUsageRefreshSettings(
    /** Legacy field/key name; the setting now means sync when the app enters foreground. */
    val autoSyncOnOpen: Boolean = true,
    val backgroundSyncEnabled: Boolean = false,
    val intervalMinutes: Int = DEFAULT_INTERVAL_MINUTES,
) {
    val normalizedIntervalMinutes: Int
        get() = if (intervalMinutes in SUPPORTED_INTERVAL_MINUTES) {
            intervalMinutes
        } else {
            DEFAULT_INTERVAL_MINUTES
        }

    companion object {
        const val DEFAULT_INTERVAL_MINUTES = 15
        val SUPPORTED_INTERVAL_MINUTES = listOf(15, 30, 60)
    }
}

class TokenUsageRefreshSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): TokenUsageRefreshSettings = TokenUsageRefreshSettings(
        autoSyncOnOpen = preferences.getBoolean(KEY_AUTO_SYNC_ON_OPEN, true),
        backgroundSyncEnabled = preferences.getBoolean(KEY_BACKGROUND_SYNC_ENABLED, false),
        intervalMinutes = preferences.getInt(
            KEY_INTERVAL_MINUTES,
            TokenUsageRefreshSettings.DEFAULT_INTERVAL_MINUTES,
        ),
    )

    fun save(settings: TokenUsageRefreshSettings) {
        preferences.edit()
            .putBoolean(KEY_AUTO_SYNC_ON_OPEN, settings.autoSyncOnOpen)
            .putBoolean(KEY_BACKGROUND_SYNC_ENABLED, settings.backgroundSyncEnabled)
            .putInt(KEY_INTERVAL_MINUTES, settings.normalizedIntervalMinutes)
            .apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "token_usage_refresh_settings"
        private const val KEY_AUTO_SYNC_ON_OPEN = "auto_sync_on_open"
        private const val KEY_BACKGROUND_SYNC_ENABLED = "background_sync_enabled"
        private const val KEY_INTERVAL_MINUTES = "interval_minutes"
    }
}
