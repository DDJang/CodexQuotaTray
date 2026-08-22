package com.codexquotatray.android.alerts

import android.content.Context
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.protocol.QuotaWindow
import java.security.MessageDigest
import kotlin.math.max

data class AlertRecord(
    val lastRemainingPercent: Int? = null,
    val lastResetAt: Long? = null,
    val lastWindowDurationMins: Long? = null,
    val notified50: Boolean = false,
    val notified20: Boolean = false,
    val notified10: Boolean = false,
)

data class ResetCreditAlertRecord(
    val lastSeenMillis: Long? = null,
    val expiresAtMillis: Long? = null,
    val notified: Boolean = false,
)

enum class AlertEventKind {
    THRESHOLD,
    RESET,
    RESET_CREDIT_EXPIRY,
}

data class QuotaAlertEvent(
    val kind: AlertEventKind,
    val window: QuotaWindow,
    val threshold: Int? = null,
    val resetCredit: ResetCredit? = null,
)

interface AlertStateStore {
    fun load(windowKey: String): AlertRecord?
    fun save(windowKey: String, record: AlertRecord)
    fun clearWindow(windowKey: String)
    fun markSuccessfulRefresh(nowMillis: Long)
    fun lastSuccessfulRefresh(): Long?
    fun loadResetCredit(fingerprint: String): ResetCreditAlertRecord? = null
    fun saveResetCredit(fingerprint: String, record: ResetCreditAlertRecord) = Unit
    fun clearResetCredit(fingerprint: String) = Unit
    fun resetCreditKeys(): Set<String> = emptySet()
}

class QuotaAlertStateStore(context: Context) : AlertStateStore {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )

    @Synchronized
    override fun load(windowKey: String): AlertRecord? {
        val prefix = keyPrefix(windowKey)
        if (!preferences.contains(prefix + LAST_REMAINING)) return null
        return AlertRecord(
            lastRemainingPercent = preferences.getInt(prefix + LAST_REMAINING, UNKNOWN_INT)
                .takeIf { it != UNKNOWN_INT },
            lastResetAt = preferences.getLong(prefix + LAST_RESET_AT, 0L).takeIf { it > 0L },
            lastWindowDurationMins = preferences
                .getLong(prefix + DURATION, 0L)
                .takeIf { it > 0L },
            notified50 = preferences.getBoolean(prefix + NOTIFIED_50, false),
            notified20 = preferences.getBoolean(prefix + NOTIFIED_20, false),
            notified10 = preferences.getBoolean(prefix + NOTIFIED_10, false),
        )
    }

    @Synchronized
    override fun save(windowKey: String, record: AlertRecord) {
        val prefix = keyPrefix(windowKey)
        preferences.edit()
            .putInt(prefix + LAST_REMAINING, record.lastRemainingPercent ?: UNKNOWN_INT)
            .putLong(prefix + LAST_RESET_AT, record.lastResetAt ?: 0L)
            .putLong(prefix + DURATION, record.lastWindowDurationMins ?: 0L)
            .putBoolean(prefix + NOTIFIED_50, record.notified50)
            .putBoolean(prefix + NOTIFIED_20, record.notified20)
            .putBoolean(prefix + NOTIFIED_10, record.notified10)
            .commit()
    }

    @Synchronized
    override fun clearWindow(windowKey: String) {
        val prefix = keyPrefix(windowKey)
        preferences.edit()
            .remove(prefix + LAST_REMAINING)
            .remove(prefix + LAST_RESET_AT)
            .remove(prefix + DURATION)
            .remove(prefix + NOTIFIED_50)
            .remove(prefix + NOTIFIED_20)
            .remove(prefix + NOTIFIED_10)
            .commit()
    }

    @Synchronized
    override fun loadResetCredit(fingerprint: String): ResetCreditAlertRecord? {
        val prefix = creditKeyPrefix(fingerprint)
        if (!preferences.contains(prefix + CREDIT_NOTIFIED)) return null
        return ResetCreditAlertRecord(
            lastSeenMillis = preferences.getLong(prefix + CREDIT_LAST_SEEN, 0L).takeIf { it > 0L },
            expiresAtMillis = preferences.getLong(prefix + CREDIT_EXPIRES_AT, 0L).takeIf { it > 0L },
            notified = preferences.getBoolean(prefix + CREDIT_NOTIFIED, false),
        )
    }

    @Synchronized
    override fun saveResetCredit(fingerprint: String, record: ResetCreditAlertRecord) {
        val prefix = creditKeyPrefix(fingerprint)
        preferences.edit()
            .putLong(prefix + CREDIT_LAST_SEEN, record.lastSeenMillis ?: 0L)
            .putLong(prefix + CREDIT_EXPIRES_AT, record.expiresAtMillis ?: 0L)
            .putBoolean(prefix + CREDIT_NOTIFIED, record.notified)
            .commit()
    }

    @Synchronized
    override fun clearResetCredit(fingerprint: String) {
        val prefix = creditKeyPrefix(fingerprint)
        preferences.edit()
            .remove(prefix + CREDIT_LAST_SEEN)
            .remove(prefix + CREDIT_EXPIRES_AT)
            .remove(prefix + CREDIT_NOTIFIED)
            .commit()
    }

    override fun resetCreditKeys(): Set<String> = preferences.all.keys
        .filter { it.startsWith(CREDIT_PREFIX) }
        .map { it.removePrefix(CREDIT_PREFIX).substringBefore('_') }
        .toSet()

    override fun markSuccessfulRefresh(nowMillis: Long) {
        preferences.edit().putLong(KEY_LAST_SUCCESSFUL_REFRESH, nowMillis).commit()
    }

    override fun lastSuccessfulRefresh(): Long? = preferences
        .getLong(KEY_LAST_SUCCESSFUL_REFRESH, 0L)
        .takeIf { it > 0L }

    fun clear() {
        preferences.edit().clear().commit()
    }

    private fun keyPrefix(windowKey: String): String = "window_${stableKey(windowKey)}_"

    companion object {
        private const val PREFERENCES_NAME = "quota_alert_state"
        private const val KEY_LAST_SUCCESSFUL_REFRESH = "last_successful_refresh"
        private const val LAST_REMAINING = "remaining"
        private const val LAST_RESET_AT = "reset_at"
        private const val DURATION = "duration"
        private const val NOTIFIED_50 = "notified_50"
        private const val NOTIFIED_20 = "notified_20"
        private const val NOTIFIED_10 = "notified_10"
        private const val CREDIT_PREFIX = "credit_"
        private const val CREDIT_LAST_SEEN = "last_seen"
        private const val CREDIT_EXPIRES_AT = "expires_at"
        private const val CREDIT_NOTIFIED = "notified"
        private const val UNKNOWN_INT = -1

        internal fun stableKey(window: QuotaWindow): String = stableKey(
            window.limitId
                ?: listOf(window.limitName, window.sourceSlot, window.windowDurationMins).joinToString("|")
        )

        private fun stableKey(value: String): String {
            val digest = MessageDigest.getInstance("SHA-256")
                .digest(value.toByteArray(Charsets.UTF_8))
            return digest.joinToString("") { byte -> "%02x".format(byte) }.take(24)
        }

        private fun creditKeyPrefix(fingerprint: String): String = "$CREDIT_PREFIX${fingerprint}_"
    }
}

class QuotaAlertEvaluator(
    private val stateStore: AlertStateStore,
) {
    private val lastEvaluationPrevious = linkedMapOf<String, AlertRecord?>()
    private val lastResetCreditEvaluationPrevious = linkedMapOf<String, ResetCreditAlertRecord?>()

    fun evaluate(windows: List<QuotaWindow>): List<QuotaAlertEvent> {
        val events = mutableListOf<QuotaAlertEvent>()
        lastEvaluationPrevious.clear()
        for (window in windows) {
            val key = QuotaAlertStateStore.stableKey(window)
            val previous = stateStore.load(key)
            lastEvaluationPrevious.putIfAbsent(key, previous)
            val current = window.remainingPercent
            if (current == null) {
                stateStore.save(
                    key,
                    AlertRecord(
                        lastRemainingPercent = previous?.lastRemainingPercent,
                        lastResetAt = window.resetsAt ?: previous?.lastResetAt,
                        lastWindowDurationMins = window.windowDurationMins
                            ?: previous?.lastWindowDurationMins,
                        notified50 = previous?.notified50 ?: false,
                        notified20 = previous?.notified20 ?: false,
                        notified10 = previous?.notified10 ?: false,
                    ),
                )
                continue
            }

            val resetObserved = previous != null && isCycleTransition(previous, window)
            if (resetObserved) {
                stateStore.save(
                    key,
                    AlertRecord(
                        lastRemainingPercent = current,
                        lastResetAt = window.resetsAt ?: previous.lastResetAt,
                        lastWindowDurationMins = window.windowDurationMins,
                    ),
                )
                events += QuotaAlertEvent(AlertEventKind.RESET, window)
                continue
            }

            var notified50 = previous?.notified50 ?: false
            var notified20 = previous?.notified20 ?: false
            var notified10 = previous?.notified10 ?: false
            val old = previous?.lastRemainingPercent
            if (old == null) {
                notified50 = current <= 50
                notified20 = current <= 20
                notified10 = current <= 10
            } else {
                val crossed = buildList {
                    if (!notified50 && old > 50 && current <= 50) add(50)
                    if (!notified20 && old > 20 && current <= 20) add(20)
                    if (!notified10 && old > 10 && current <= 10) add(10)
                }
                crossed.minOrNull()?.let { mostSevere ->
                    // One refresh can cross several levels; emit only the
                    // most severe notification while consuming all crossed
                    // thresholds so they cannot replay later.
                    events += QuotaAlertEvent(AlertEventKind.THRESHOLD, window, mostSevere)
                }
                crossed.forEach { threshold ->
                    when (threshold) {
                        50 -> notified50 = true
                        20 -> notified20 = true
                        10 -> notified10 = true
                    }
                }
            }
            stateStore.save(
                key,
                AlertRecord(
                    lastRemainingPercent = current,
                    lastResetAt = window.resetsAt ?: previous?.lastResetAt,
                    lastWindowDurationMins = window.windowDurationMins
                        ?: previous?.lastWindowDurationMins,
                    notified50 = notified50,
                    notified20 = notified20,
                    notified10 = notified10,
                ),
            )
        }
        return events
    }

    fun evaluateResetCredits(
        credits: List<ResetCredit>?,
        settings: QuotaAlertSettings,
        nowMillis: Long = System.currentTimeMillis(),
        availableCount: Long? = null,
    ): List<QuotaAlertEvent> {
        lastResetCreditEvaluationPrevious.clear()
        if (!settings.resetCreditExpiryEnabled) return emptyList()

        val nowSeconds = nowMillis / 1_000L
        val leadMillis = QuotaAlertSettingsStore.normalizeLeadHours(settings.resetCreditExpiryLeadHours) * 3_600_000L
        val current = if (availableCount == 0L) {
            emptyMap()
        } else {
            credits.orEmpty()
            .asSequence()
            .filter { credit ->
                credit.status?.trim()?.equals("available", ignoreCase = true) == true
                    && credit.expiresAt != null
                    && credit.expiresAt > nowSeconds
            }
            .associateBy { credit -> ResetCreditFingerprint.create(credit) }
        }
        val events = mutableListOf<QuotaAlertEvent>()
        current.forEach { (fingerprint, credit) ->
            val previous = stateStore.loadResetCredit(fingerprint)
            lastResetCreditEvaluationPrevious.putIfAbsent(fingerprint, previous)
            val expiresAtMillis = credit.expiresAt!! * 1_000L
            val dueAtMillis = expiresAtMillis - leadMillis
            val alreadyNotified = previous?.notified == true
            val shouldNotify = !alreadyNotified && nowMillis >= dueAtMillis
            stateStore.saveResetCredit(
                fingerprint,
                ResetCreditAlertRecord(
                    lastSeenMillis = nowMillis,
                    expiresAtMillis = expiresAtMillis,
                    notified = alreadyNotified || shouldNotify,
                ),
            )
            if (shouldNotify) {
                events += QuotaAlertEvent(
                    kind = AlertEventKind.RESET_CREDIT_EXPIRY,
                    window = QuotaWindow(
                        limitId = null,
                        limitName = credit.title ?: credit.resetType ?: "重置卡",
                        sourceSlot = "reset_credit",
                        usedPercent = null,
                        remainingPercent = null,
                        windowDurationMins = null,
                        resetsAt = credit.expiresAt,
                    ),
                    resetCredit = credit,
                )
            }
        }

        val staleBefore = nowMillis - 30L * 24L * 3_600_000L
        stateStore.resetCreditKeys()
            .filterNot(current::containsKey)
            .forEach { fingerprint ->
                val previous = stateStore.loadResetCredit(fingerprint) ?: return@forEach
                if (previous.expiresAtMillis?.let { it <= nowMillis } == true
                    || previous.lastSeenMillis == null
                    || previous.lastSeenMillis < staleBefore) {
                    lastResetCreditEvaluationPrevious.putIfAbsent(fingerprint, previous)
                    stateStore.clearResetCredit(fingerprint)
                }
            }
        stateStore.resetCreditKeys()
            .mapNotNull { fingerprint ->
                stateStore.loadResetCredit(fingerprint)?.let { fingerprint to it }
            }
            .sortedBy { it.second.lastSeenMillis ?: Long.MIN_VALUE }
            .take((stateStore.resetCreditKeys().size - 128).coerceAtLeast(0))
            .forEach { (fingerprint, previous) ->
                lastResetCreditEvaluationPrevious.putIfAbsent(fingerprint, previous)
                stateStore.clearResetCredit(fingerprint)
            }
        return events
    }

    /** Restores the state captured by the most recent evaluation. */
    fun restoreLastEvaluation() {
        lastEvaluationPrevious.forEach { (key, previous) ->
            if (previous == null) stateStore.clearWindow(key) else stateStore.save(key, previous)
        }
        lastEvaluationPrevious.clear()
        lastResetCreditEvaluationPrevious.forEach { (fingerprint, previous) ->
            if (previous == null) stateStore.clearResetCredit(fingerprint)
            else stateStore.saveResetCredit(fingerprint, previous)
        }
        lastResetCreditEvaluationPrevious.clear()
    }

    private fun isCycleTransition(previous: AlertRecord, window: QuotaWindow): Boolean {
        val current = window.remainingPercent
        val strongRecovery = previous.lastRemainingPercent?.let { old ->
            current != null && current >= 80 && current - old >= 50
        } ?: false
        if (strongRecovery) return true

        val oldReset = previous.lastResetAt ?: return false
        val newReset = window.resetsAt ?: return false
        if (newReset <= oldReset) return false
        val previousDurationMins = previous.lastWindowDurationMins ?: return false
        val currentDurationMins = window.windowDurationMins ?: return false
        if (previousDurationMins <= 0L
            || currentDurationMins <= 0L
            || previousDurationMins != currentDurationMins) {
            return false
        }

        val durationSeconds = currentDurationMins * 60L
        val resetAdvance = newReset - oldReset
        // Keep resetAt reliability identical to Windows: matching positive
        // durations and at least half a window (with a five-minute floor).
        return resetAdvance >= max(300L, durationSeconds / 2L)
    }
}
