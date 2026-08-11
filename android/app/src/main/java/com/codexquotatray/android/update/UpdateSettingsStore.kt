package com.codexquotatray.android.update

import android.content.Context

data class UpdateSettings(
    val source: UpdateSource = UpdateSource.GITHUB,
    val automaticChecksEnabled: Boolean = true,
    val updateRemindersEnabled: Boolean = true,
    val lastCheckAtMillis: Long = 0L,
    val lastNotifiedVersion: String? = null,
)

interface UpdateSettingsRepository {
    fun load(): UpdateSettings
    fun save(settings: UpdateSettings)
}

class UpdateSettingsStore(context: Context) : UpdateSettingsRepository {
    private val preferences = context.applicationContext.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE)

    override fun load(): UpdateSettings = UpdateSettings(
        source = preferences.getString(KEY_SOURCE, null)
            ?.let { stored -> UpdateSource.entries.firstOrNull { it.name == stored } }
            ?: UpdateSource.GITHUB,
        automaticChecksEnabled = preferences.getBoolean(KEY_AUTOMATIC, true),
        updateRemindersEnabled = preferences.getBoolean(KEY_REMINDERS, true),
        lastCheckAtMillis = preferences.getLong(KEY_LAST_CHECK, 0L).coerceAtLeast(0L),
        lastNotifiedVersion = preferences.getString(KEY_LAST_NOTIFIED, null),
    )

    override fun save(settings: UpdateSettings) {
        preferences.edit()
            .putString(KEY_SOURCE, settings.source.name)
            .putBoolean(KEY_AUTOMATIC, settings.automaticChecksEnabled)
            .putBoolean(KEY_REMINDERS, settings.updateRemindersEnabled)
            .putLong(KEY_LAST_CHECK, settings.lastCheckAtMillis.coerceAtLeast(0L))
            .apply {
                if (settings.lastNotifiedVersion == null) remove(KEY_LAST_NOTIFIED)
                else putString(KEY_LAST_NOTIFIED, settings.lastNotifiedVersion)
            }
            .apply()
    }

    companion object {
        private const val PREFERENCES = "codex_update_settings"
        private const val KEY_SOURCE = "source"
        private const val KEY_AUTOMATIC = "automatic_checks_enabled"
        private const val KEY_REMINDERS = "update_reminders_enabled"
        private const val KEY_LAST_CHECK = "last_check_at_millis"
        private const val KEY_LAST_NOTIFIED = "last_notified_version"
    }
}
