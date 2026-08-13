package com.codexquotatray.android.quota

import com.codexquotatray.android.alerts.QuotaAlertEvent
import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.protocol.QuotaWindow

/** The one success path shared by Direct OpenAI and Windows LAN quota results. */
internal class QuotaSuccessfulRefreshCommitter(
    private val saveSnapshot: (DirectQuotaResult, Long, String?) -> Unit,
    private val evaluateAlerts: (List<QuotaWindow>) -> List<QuotaAlertEvent>,
    private val markSuccessfulRefresh: (Long) -> Unit,
    private val publishNotifications: (List<QuotaAlertEvent>) -> Boolean,
    private val restoreAlerts: () -> Unit = {},
    private val nowMillis: () -> Long = System::currentTimeMillis,
) {
    fun commit(result: DirectQuotaResult, windowsDeviceIdentity: String? = null): Boolean {
        if (result.quotaState == "unavailable") return false
        val completedAtMillis = nowMillis()
        saveSnapshot(result, completedAtMillis, windowsDeviceIdentity)
        val events = evaluateAlerts(result.windows)
        markSuccessfulRefresh(completedAtMillis)
        val published = try {
            publishNotifications(events)
        } catch (_: Exception) {
            false
        }
        if (events.isNotEmpty() && !published) {
            restoreAlerts()
        }
        return true
    }
}
