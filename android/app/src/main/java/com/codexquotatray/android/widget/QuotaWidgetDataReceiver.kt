package com.codexquotatray.android.widget

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class QuotaWidgetDataReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val store = QuotaWidgetStore(context)
        when (intent.action) {
            ACTION_UPDATE -> {
                val projection = intent.getStringExtra(EXTRA_PROJECTION)
                    ?.let(QuotaWidgetProjectionCodec::decode)
                val stored = if (projection == null) {
                    store.clear()
                } else {
                    store.save(projection)
                }
                if (stored) QuotaWidgetRenderer.updateAll(context)
            }
            ACTION_CLEAR -> {
                if (store.clear()) QuotaWidgetRenderer.updateAll(context)
            }
        }
    }

    companion object {
        const val ACTION_UPDATE = "com.codexquotatray.android.widget.UPDATE"
        const val ACTION_CLEAR = "com.codexquotatray.android.widget.CLEAR"
        const val EXTRA_PROJECTION = "projection"
    }
}
