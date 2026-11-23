package com.codexquotatray.android

import android.content.Context

internal enum class HomeRefreshBehavior(val label: String) {
    CURRENT_PAGE("仅刷新当前页"),
    QUOTA_AND_STATISTICS("同时刷新额度与统计"),
}

internal class HomeRefreshSettingsStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        "home_refresh_settings",
        Context.MODE_PRIVATE,
    )

    fun load(): HomeRefreshBehavior =
        if (preferences.getBoolean(KEY_REFRESH_BOTH, false)) HomeRefreshBehavior.QUOTA_AND_STATISTICS
        else HomeRefreshBehavior.CURRENT_PAGE

    fun save(behavior: HomeRefreshBehavior) {
        preferences.edit()
            .putBoolean(KEY_REFRESH_BOTH, behavior == HomeRefreshBehavior.QUOTA_AND_STATISTICS)
            .apply()
    }

    private companion object {
        const val KEY_REFRESH_BOTH = "refresh_both"
    }
}
