package com.codexquotatray.android.widget

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent

class QuotaWidgetProvider : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        QuotaWidgetRenderer.update(context, ids)
    }

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        if (intent.action == ACTION_XIAOMI_UPDATE) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = intent.getIntArrayExtra(AppWidgetManager.EXTRA_APPWIDGET_IDS)
                ?.takeIf { it.isNotEmpty() }
                ?: manager.getAppWidgetIds(ComponentName(context, QuotaWidgetProvider::class.java))
            QuotaWidgetRenderer.update(context, ids)
        }
    }

    override fun onDisabled(context: Context) {
        QuotaWidgetStore(context).clear()
    }

    companion object {
        const val ACTION_XIAOMI_UPDATE = "miui.appwidget.action.APPWIDGET_UPDATE"
    }
}
