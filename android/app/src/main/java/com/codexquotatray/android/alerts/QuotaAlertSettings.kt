package com.codexquotatray.android.alerts

import android.content.Context

/** User-visible switches for the two independent quota notification kinds. */
data class QuotaAlertSettings(
    val lowQuotaEnabled: Boolean = true,
    val resetEnabled: Boolean = true,
    val resetCreditExpiryEnabled: Boolean = false,
    val resetCreditExpiryLeadHours: Int = 24,
)

class QuotaAlertSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    fun load(): QuotaAlertSettings = QuotaAlertSettings(
        lowQuotaEnabled = preferences.getBoolean(KEY_LOW_QUOTA, true),
        resetEnabled = preferences.getBoolean(KEY_RESET, true),
        resetCreditExpiryEnabled = preferences.getBoolean(KEY_RESET_CREDIT_EXPIRY, false),
        resetCreditExpiryLeadHours = normalizeLeadHours(
            preferences.getInt(KEY_RESET_CREDIT_EXPIRY_LEAD_HOURS, 24),
        ),
    )

    fun save(settings: QuotaAlertSettings) {
        preferences.edit()
            .putBoolean(KEY_LOW_QUOTA, settings.lowQuotaEnabled)
            .putBoolean(KEY_RESET, settings.resetEnabled)
            .putBoolean(KEY_RESET_CREDIT_EXPIRY, settings.resetCreditExpiryEnabled)
            .putInt(KEY_RESET_CREDIT_EXPIRY_LEAD_HOURS, normalizeLeadHours(settings.resetCreditExpiryLeadHours))
            .apply()
    }

    companion object {
        private const val PREFERENCES_NAME = "quota_alert_settings"
        private const val KEY_LOW_QUOTA = "low_quota_enabled"
        private const val KEY_RESET = "reset_enabled"
        private const val KEY_RESET_CREDIT_EXPIRY = "reset_credit_expiry_enabled"
        private const val KEY_RESET_CREDIT_EXPIRY_LEAD_HOURS = "reset_credit_expiry_lead_hours"

        internal fun normalizeLeadHours(value: Int): Int = when (value) {
            6 -> 6
            1 -> 1
            else -> 24
        }
    }
}

fun filterEnabledAlertEvents(
    events: List<QuotaAlertEvent>,
    settings: QuotaAlertSettings,
): List<QuotaAlertEvent> = events.filter { event ->
    when (event.kind) {
        AlertEventKind.THRESHOLD -> settings.lowQuotaEnabled
        AlertEventKind.RESET -> settings.resetEnabled
        AlertEventKind.RESET_CREDIT_EXPIRY -> settings.resetCreditExpiryEnabled
    }
}
