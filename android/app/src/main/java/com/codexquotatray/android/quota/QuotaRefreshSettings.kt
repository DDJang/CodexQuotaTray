package com.codexquotatray.android.quota

import android.content.Context

data class QuotaRefreshSettings(
    val enabled: Boolean = true,
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

class QuotaRefreshSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): QuotaRefreshSettings = QuotaRefreshSettings(
        enabled = preferences.getBoolean(KEY_ENABLED, true),
        intervalMinutes = preferences.getInt(
            KEY_INTERVAL_MINUTES,
            QuotaRefreshSettings.DEFAULT_INTERVAL_MINUTES,
        ),
    )

    fun save(settings: QuotaRefreshSettings) {
        preferences.edit()
            .putBoolean(KEY_ENABLED, settings.enabled)
            .putInt(KEY_INTERVAL_MINUTES, settings.normalizedIntervalMinutes)
            .apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "quota_refresh_settings"
        private const val KEY_ENABLED = "enabled"
        private const val KEY_INTERVAL_MINUTES = "interval_minutes"
    }
}
