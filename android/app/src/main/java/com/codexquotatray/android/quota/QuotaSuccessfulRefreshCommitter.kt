package com.codexquotatray.android.quota

import com.codexquotatray.android.alerts.QuotaAlertEvent
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaWindow

/** The one success path shared by Direct OpenAI and Windows LAN quota results. */
internal class QuotaSuccessfulRefreshCommitter(
    private val saveSnapshot: (DirectQuotaResult, Long) -> Unit,
    private val evaluateAlerts: (List<QuotaWindow>) -> List<QuotaAlertEvent>,
    private val markSuccessfulRefresh: (Long) -> Unit,
    private val publishNotifications: (List<QuotaAlertEvent>) -> Unit,
    private val nowMillis: () -> Long = System::currentTimeMillis,
) {
    fun commit(result: DirectQuotaResult): Boolean {
        if (result.quotaState == "unavailable") return false
        val completedAtMillis = nowMillis()
        saveSnapshot(result, completedAtMillis)
        val events = evaluateAlerts(result.windows)
        markSuccessfulRefresh(completedAtMillis)
        publishNotifications(events)
        return true
    }
}
