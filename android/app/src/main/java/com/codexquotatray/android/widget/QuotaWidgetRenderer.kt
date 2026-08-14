package com.codexquotatray.android.widget

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.view.View
import android.widget.RemoteViews
import com.codexquotatray.android.MainActivity
import com.codexquotatray.android.R
import com.codexquotatray.android.quotaProgressArgb
import com.codexquotatray.android.usage.TokenFormatter

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
        val ringWindows = widgetRingWindows(windows)
        val outer = ringWindows.getOrNull(0)
        val inner = ringWindows.getOrNull(1)
        val plan = projection?.planType?.takeIf {
            it.isNotBlank() && !it.equals("Codex", ignoreCase = true)
        }
        views.setTextViewText(
            R.id.widget_plan,
            plan?.let { "Codex · $it" } ?: "Codex",
        )
        views.setTextViewText(
            R.id.widget_updated,
            projection?.let { QuotaWidgetDisplayFormatter.formatUpdatedAt(it.updatedAtMillis) } ?: "",
        )
        views.setViewVisibility(R.id.widget_empty, if (outer == null) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.widget_dashboard, if (outer == null) View.GONE else View.VISIBLE)
        if (outer == null) return

        views.setImageViewBitmap(
            R.id.widget_quota_rings,
            QuotaWidgetRingRenderer.render(context, outer, inner),
        )
        views.setViewVisibility(R.id.widget_quota_single_label, if (inner == null) View.VISIBLE else View.GONE)
        views.setViewVisibility(R.id.widget_quota_double_labels, if (inner == null) View.GONE else View.VISIBLE)
        views.setViewVisibility(R.id.widget_quota_center_caption, if (inner == null) View.VISIBLE else View.GONE)
        if (inner == null) {
            views.setTextViewText(R.id.widget_quota_single_label, compactTitle(outer.title))
            views.setTextViewText(R.id.widget_quota_center_value, formatPercent(outer))
            views.setTextColor(R.id.widget_quota_center_value, quotaColor(context, outer))
            views.setTextViewText(R.id.widget_quota_center_caption, "余额")
        } else {
            views.setTextViewText(R.id.widget_quota_outer_label, formatQuotaLabel(outer))
            views.setTextViewText(R.id.widget_quota_inner_label, formatQuotaLabel(inner))
            views.setTextColor(R.id.widget_quota_outer_label, quotaColor(context, outer))
            views.setTextColor(R.id.widget_quota_inner_label, quotaColor(context, inner))
            views.setTextViewText(R.id.widget_quota_center_value, "额度")
        }

        val tokenSummary = projection?.tokenSummary
        views.setViewVisibility(R.id.widget_token_stats, if (tokenSummary == null) View.GONE else View.VISIBLE)
        views.setViewVisibility(R.id.widget_token_empty, if (tokenSummary == null) View.VISIBLE else View.GONE)
        if (tokenSummary != null) {
            views.setTextViewText(R.id.widget_token_today, TokenFormatter.format(tokenSummary.todayTokens))
            views.setTextViewText(R.id.widget_token_week, TokenFormatter.format(tokenSummary.last7DaysTokens))
            views.setTextViewText(
                R.id.widget_token_month,
                tokenSummary.last30DaysTokens?.let(TokenFormatter::format) ?: "—",
            )
            views.setTextViewText(R.id.widget_token_lifetime, TokenFormatter.format(tokenSummary.lifetimeTokens))
        }
    }

    private fun formatQuotaLabel(window: QuotaWidgetWindow): String =
        "${compactTitle(window.title)} · ${formatPercent(window)}"

    private fun compactTitle(title: String): String = title.replace(" ", "")

    private fun formatPercent(window: QuotaWidgetWindow): String =
        window.remainingPercent?.coerceIn(0, 100)?.let { "$it%" } ?: "不可用"

    private fun quotaColor(context: Context, window: QuotaWidgetWindow): Int =
        window.remainingPercent?.coerceIn(0, 100)?.let(::quotaProgressArgb)
            ?: context.getColor(R.color.widget_secondary_text)

    internal fun widgetIds(context: Context): IntArray = widgetIds(
        context,
        AppWidgetManager.getInstance(context),
    )

    private fun widgetIds(context: Context, manager: AppWidgetManager): IntArray =
        manager.getAppWidgetIds(ComponentName(context, QuotaWidgetProvider::class.java))
}
