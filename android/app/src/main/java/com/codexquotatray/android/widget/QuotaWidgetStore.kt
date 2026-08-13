package com.codexquotatray.android.widget

import android.content.Context

/** Used only by receivers running in the dedicated :widgetProvider process. */
internal class QuotaWidgetStore(context: Context) {
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)

    fun load(): QuotaWidgetProjection? = preferences.getString(KEY_PROJECTION, null)
        ?.let(QuotaWidgetProjectionCodec::decode)

    fun save(projection: QuotaWidgetProjection): Boolean = preferences.edit()
        .putString(KEY_PROJECTION, QuotaWidgetProjectionCodec.encode(projection))
        .commit()

    fun clear(): Boolean = preferences.edit().remove(KEY_PROJECTION).commit()

    companion object {
        private const val PREFERENCES_NAME = "quota_widget_projection"
        private const val KEY_PROJECTION = "projection"
    }
}
