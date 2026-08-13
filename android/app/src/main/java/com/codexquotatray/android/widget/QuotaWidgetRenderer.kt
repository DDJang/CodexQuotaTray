package com.codexquotatray.android.widget

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.widget.RemoteViews
import com.codexquotatray.android.MainActivity
import com.codexquotatray.android.R

internal object QuotaWidgetRenderer {
    fun updateAll(context: Context) {
        val manager = AppWidgetManager.getInstance(context)
        val ids = widgetIds(context, manager)
        if (ids.isNotEmpty()) update(context, manager, ids)
    }

    fun update(context: Context, ids: IntArray) {
        if (ids.isEmpty()) return
        update(context, AppWidgetManager.getInstance(context), ids)
    }

    private fun update(context: Context, manager: AppWidgetManager, ids: IntArray) {
        val views = RemoteViews(context.packageName, R.layout.widget_quota)
        val projection = QuotaWidgetStore(context).load()
        render(context, views, projection)
        manager.updateAppWidget(ids, views)
    }

    private fun render(context: Context, views: RemoteViews, projection: QuotaWidgetProjection?) {
        val clickIntent = Intent(context, MainActivity::class.java)
        val pendingIntent = android.app.PendingIntent.getActivity(
            context,
            0,
            clickIntent,
            android.app.PendingIntent.FLAG_UPDATE_CURRENT or android.app.PendingIntent.FLAG_IMMUTABLE,
        )
        views.setOnClickPendingIntent(android.R.id.background, pendingIntent)

        val windows = projection?.windows.orEmpty()
        views.setTextViewText(R.id.widget_plan, projection?.planType?.let { "Codex · $it" } ?: "Codex")
        views.setTextViewText(
            R.id.widget_updated,
            projection?.let { QuotaWidgetDisplayFormatter.formatUpdatedAt(it.updatedAtMillis) } ?: "",
        )
        views.setViewVisibility(R.id.widget_empty, if (windows.isEmpty()) android.view.View.VISIBLE else android.view.View.GONE)
        renderWindow(views, R.id.widget_primary_row, R.id.widget_primary_title, R.id.widget_primary_percent, R.id.widget_primary_progress, R.id.widget_primary_reset, windows.getOrNull(0))
        renderWindow(views, R.id.widget_secondary_row, R.id.widget_secondary_title, R.id.widget_secondary_percent, R.id.widget_secondary_progress, R.id.widget_secondary_reset, windows.getOrNull(1))
    }

    private fun renderWindow(
        views: RemoteViews,
        rowId: Int,
        titleId: Int,
        percentId: Int,
        progressId: Int,
        resetId: Int,
        window: QuotaWidgetWindow?,
    ) {
        views.setViewVisibility(rowId, if (window == null) android.view.View.GONE else android.view.View.VISIBLE)
        if (window == null) return
        views.setTextViewText(titleId, window.title)
        views.setTextViewText(percentId, window.remainingPercent?.let { "${it.coerceIn(0, 100)}%" } ?: "不可用")
        views.setViewVisibility(progressId, if (window.remainingPercent == null) android.view.View.GONE else android.view.View.VISIBLE)
        window.remainingPercent?.let { views.setProgressBar(progressId, 100, it.coerceIn(0, 100), false) }
        views.setTextViewText(resetId, QuotaWidgetDisplayFormatter.formatResetAt(window.resetsAt, System.currentTimeMillis()))
    }

    internal fun widgetIds(context: Context): IntArray = widgetIds(
        context,
        AppWidgetManager.getInstance(context),
    )

    private fun widgetIds(context: Context, manager: AppWidgetManager): IntArray =
        manager.getAppWidgetIds(ComponentName(context, QuotaWidgetProvider::class.java))
}
