package com.codexquotatray.android.widget

import android.appwidget.AppWidgetManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.usage.TokenUsageCache
import com.codexquotatray.android.usage.TokenUsageSummary
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.cacheIdentity
import com.codexquotatray.android.auth.OAuthStore

object QuotaWidgetBridge {
    fun publish(
        context: Context,
        result: DirectQuotaResult,
    ) {
        if (!hasWidgets(context)) return
        sendUpdate(
            context,
            QuotaWidgetProjection.fromResult(
                result,
                result.updatedAtMillis,
                tokenSummary(context.applicationContext),
            ),
        )
    }

    fun syncFromCurrentMainSnapshot(context: Context) {
        if (!hasWidgets(context)) return
        val appContext = context.applicationContext
        val pairingIdentity = TokenSyncStore(appContext).load()?.cacheIdentity()
        val snapshot = QuotaSnapshotStore(appContext).load(pairingIdentity)
        if (snapshot == null || snapshot.quotaState == "unavailable") {
            sendClear(appContext)
        } else {
            sendUpdate(
                appContext,
                QuotaWidgetProjection.fromResult(
                    snapshot,
                    snapshot.updatedAtMillis,
                    tokenSummary(appContext),
                ),
            )
        }
    }

    private fun tokenSummary(context: Context): QuotaWidgetTokenSummary? {
        val pairing = TokenSyncStore(context).load()
        val hasOAuth = OAuthStore(context).load() != null
        return TokenUsageCache(context).loadForAvailableSources(pairing, hasOAuth)?.summary?.let { summary ->
            summary.toQuotaWidgetTokenSummary()
        }
    }

    private fun sendUpdate(context: Context, projection: QuotaWidgetProjection) {
        context.sendBroadcast(
            Intent(context, QuotaWidgetDataReceiver::class.java)
                .setAction(QuotaWidgetDataReceiver.ACTION_UPDATE)
                .putExtra(
                    QuotaWidgetDataReceiver.EXTRA_PROJECTION,
                    QuotaWidgetProjectionCodec.encode(projection),
                ),
        )
    }

    private fun sendClear(context: Context) {
        context.sendBroadcast(
            Intent(context, QuotaWidgetDataReceiver::class.java)
                .setAction(QuotaWidgetDataReceiver.ACTION_CLEAR),
        )
    }

    private fun hasWidgets(context: Context): Boolean {
        val manager = AppWidgetManager.getInstance(context)
        return manager.getAppWidgetIds(
            ComponentName(context, QuotaWidgetProvider::class.java),
        ).isNotEmpty()
    }
}

internal fun TokenUsageSummary.toQuotaWidgetTokenSummary(): QuotaWidgetTokenSummary? =
    QuotaWidgetTokenSummary(
        todayTokens = todayTokens ?: return null,
        last7DaysTokens = last7DaysTokens ?: return null,
        last30DaysTokens = last30DaysTokens,
        lifetimeTokens = lifetimeTokens ?: return null,
    )
