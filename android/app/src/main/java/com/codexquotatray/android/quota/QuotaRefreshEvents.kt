package com.codexquotatray.android.quota

import android.content.Context
import android.content.Intent

object QuotaRefreshEvents {
    const val ACTION_COMPLETED = "com.codexquotatray.android.QUOTA_REFRESH_COMPLETED"

    fun notifyCompleted(context: Context) {
        context.sendBroadcast(
            Intent(ACTION_COMPLETED).setPackage(context.packageName),
        )
    }
}
