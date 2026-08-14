package com.codexquotatray.android.widget

import com.codexquotatray.android.protocol.DirectQuotaResult
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.toQuotaUiModel
import org.json.JSONObject

data class QuotaWidgetWindow(
    val title: String,
    val remainingPercent: Int?,
    val resetsAt: Long?,
    val windowDurationMins: Long?,
)

data class QuotaWidgetTokenSummary(
    val todayTokens: Long,
    val last7DaysTokens: Long,
    val lifetimeTokens: Long,
)

data class QuotaWidgetProjection(
    val planType: String,
    val updatedAtMillis: Long,
    val primary: QuotaWidgetWindow?,
    val secondary: QuotaWidgetWindow?,
    val tokenSummary: QuotaWidgetTokenSummary? = null,
) {
    val windows: List<QuotaWidgetWindow>
        get() = listOfNotNull(primary, secondary)

    companion object {
        fun fromResult(
            result: DirectQuotaResult,
            updatedAtMillis: Long,
            tokenSummary: QuotaWidgetTokenSummary? = null,
        ): QuotaWidgetProjection {
            val model = result.toQuotaUiModel()
            val cards = if (model.status == QuotaUiStatus.LOADED) {
                model.windows.take(2)
            } else {
                emptyList()
            }
            val windows = cards.map { card ->
                QuotaWidgetWindow(
                    title = card.title.removeSuffix("额度").takeIf(String::isNotBlank) ?: card.title,
                    remainingPercent = card.remainingPercent,
                    resetsAt = card.resetsAt,
                    windowDurationMins = card.windowDurationMins,
                )
            }
            return QuotaWidgetProjection(
                planType = model.accountLabel,
                updatedAtMillis = updatedAtMillis,
                primary = windows.getOrNull(0),
                secondary = windows.getOrNull(1),
                tokenSummary = tokenSummary,
            )
        }
    }
}

object QuotaWidgetProjectionCodec {
    private const val SCHEMA_VERSION = 1

    fun encode(projection: QuotaWidgetProjection): String = JSONObject()
        .put("schemaVersion", SCHEMA_VERSION)
        .put("planType", projection.planType)
        .put("updatedAtMillis", projection.updatedAtMillis)
        .put("primary", projection.primary?.let(::encodeWindow) ?: JSONObject.NULL)
        .put("secondary", projection.secondary?.let(::encodeWindow) ?: JSONObject.NULL)
        .put("tokenSummary", projection.tokenSummary?.let(::encodeTokenSummary) ?: JSONObject.NULL)
        .toString()

    fun decode(raw: String): QuotaWidgetProjection? = runCatching {
        val root = JSONObject(raw)
        if (root.optInt("schemaVersion", -1) != SCHEMA_VERSION) return@runCatching null
        val planType = root.optString("planType").takeIf(String::isNotBlank) ?: "Codex"
        val updatedAtMillis = root.optLong("updatedAtMillis", -1L)
            .takeIf { it > 0L }
            ?: return@runCatching null
        QuotaWidgetProjection(
            planType = planType,
            updatedAtMillis = updatedAtMillis,
            primary = decodeWindow(root.opt("primary")),
            secondary = decodeWindow(root.opt("secondary")),
            tokenSummary = decodeTokenSummary(root.opt("tokenSummary")),
        )
    }.getOrNull()

    private fun decodeTokenSummary(value: Any?): QuotaWidgetTokenSummary? {
        if (value !is JSONObject) return null
        return QuotaWidgetTokenSummary(
            todayTokens = value.optLong("todayTokens", -1L).takeIf { it >= 0L } ?: return null,
            last7DaysTokens = value.optLong("last7DaysTokens", -1L).takeIf { it >= 0L } ?: return null,
            lifetimeTokens = value.optLong("lifetimeTokens", -1L).takeIf { it >= 0L } ?: return null,
        )
    }

    private fun encodeWindow(window: QuotaWidgetWindow): JSONObject = JSONObject()
        .put("title", window.title)
        .put("remainingPercent", window.remainingPercent ?: JSONObject.NULL)
        .put("resetsAt", window.resetsAt ?: JSONObject.NULL)
        .put("windowDurationMins", window.windowDurationMins ?: JSONObject.NULL)

    private fun encodeTokenSummary(summary: QuotaWidgetTokenSummary): JSONObject = JSONObject()
        .put("todayTokens", summary.todayTokens)
        .put("last7DaysTokens", summary.last7DaysTokens)
        .put("lifetimeTokens", summary.lifetimeTokens)

    private fun decodeWindow(value: Any?): QuotaWidgetWindow? {
        if (value !is JSONObject) return null
        val title = value.optString("title").takeIf(String::isNotBlank) ?: return null
        val remaining = value.opt("remainingPercent").let { number ->
            if (number is Number) number.toInt() else null
        }
        val resetsAt = value.opt("resetsAt").let { number ->
            if (number is Number) number.toLong() else null
        }
        val duration = value.opt("windowDurationMins").let { number ->
            if (number is Number) number.toLong() else null
        }
        return QuotaWidgetWindow(title, remaining, resetsAt, duration)
    }
}

internal fun widgetRingWindows(windows: List<QuotaWidgetWindow>): List<QuotaWidgetWindow> =
    windows.take(2).sortedWith(
        compareByDescending<QuotaWidgetWindow> { it.windowDurationMins ?: Long.MIN_VALUE },
    )
