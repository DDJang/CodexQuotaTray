package com.codexquotatray.android.widget

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.Context

class QuotaWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        QuotaWidgetRenderer.update(context, ids)
    }

    override fun onDisabled(context: Context) {
        super.onDisabled(context)
        QuotaWidgetStore(context).clear()
    }
}
