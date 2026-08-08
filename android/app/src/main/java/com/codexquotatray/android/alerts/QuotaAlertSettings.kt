package com.codexquotatray.android.alerts

import android.content.Context

/** User-visible switches for the two independent quota notification kinds. */
data class QuotaAlertSettings(
    val lowQuotaEnabled: Boolean = true,
    val resetEnabled: Boolean = true,
)

class QuotaAlertSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): QuotaAlertSettings = QuotaAlertSettings(
        lowQuotaEnabled = preferences.getBoolean(KEY_LOW_QUOTA, true),
        resetEnabled = preferences.getBoolean(KEY_RESET, true),
    )

    fun save(settings: QuotaAlertSettings) {
        preferences.edit()
            .putBoolean(KEY_LOW_QUOTA, settings.lowQuotaEnabled)
            .putBoolean(KEY_RESET, settings.resetEnabled)
            .apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "quota_alert_settings"
        private const val KEY_LOW_QUOTA = "low_quota_enabled"
        private const val KEY_RESET = "reset_enabled"
    }
}

fun filterEnabledAlertEvents(
    events: List<QuotaAlertEvent>,
    settings: QuotaAlertSettings,
): List<QuotaAlertEvent> = events.filter { event ->
    when (event.kind) {
        AlertEventKind.THRESHOLD -> settings.lowQuotaEnabled
        AlertEventKind.RESET -> settings.resetEnabled
    }
}
