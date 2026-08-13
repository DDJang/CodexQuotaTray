package com.codexquotatray.android

import android.content.Intent
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.codexquotatray.android.usage.HeatmapBuckets
import com.codexquotatray.android.usage.TokenFormatter
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.matchesConfiguration
import com.codexquotatray.android.usage.TokenUsageCache
import com.codexquotatray.android.usage.TokenUsageDay
import com.codexquotatray.android.usage.TokenUsageRefreshSettingsStore
import com.codexquotatray.android.usage.TokenUsageRefreshEvents
import com.codexquotatray.android.refresh.AppAutomaticRefreshCoordinator
import com.codexquotatray.android.refresh.AutomaticRefreshChannel
import com.codexquotatray.android.refresh.AutomaticRefreshReason
import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSyncCoordinator
import com.codexquotatray.android.usage.tokenUsageSyncErrorMessage
import com.codexquotatray.android.usage.shouldForceTokenUsageRefresh
import java.text.SimpleDateFormat
import java.time.Instant
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.time.temporal.TemporalAdjusters
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

internal class TokenUsagePageController(private val host: MainActivity) {
    private val cache by lazy { TokenUsageCache(host) }
    private val store by lazy { TokenSyncStore(host) }
    private val refreshSettings by lazy { TokenUsageRefreshSettingsStore(host) }
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var destroyed = false
    private var visible = false
    private var registered = false
    var syncing by mutableStateOf(false)
        private set
    var snapshot by mutableStateOf<TokenUsageSnapshot?>(null)
        private set
    var status by mutableStateOf("尚未打开统计")
        private set
    var paired by mutableStateOf(false)
        private set

    /** The last pairing configuration reconciled into this controller. */
    private var observedPairing: TokenSyncPairing? = null

    val canSync get() = !syncing && store.load() != null

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            if (intent?.action == TokenUsageRefreshEvents.ACTION_COMPLETED && visible) {
                reconcilePairingState()
            }
        }
    }

    fun onVisible() {
        visible = true
        reconcilePairingState()
    }

    fun onForeground(reason: AutomaticRefreshReason) {
        reconcilePairingState()
        requestSync(store.load(), reason)
    }

    /**
     * Reconciles only local pairing/cache state. It never starts a network
     * request, so returning from Settings cannot resurrect a stale snapshot or
     * bypass the foreground refresh coordinator.
     */
    fun reconcilePairingState() {
        val currentPairing = store.load()
        val decision = tokenPairingReconcileDecision(
            previousPairing = observedPairing,
            currentPairing = currentPairing,
            currentSnapshot = snapshot,
            cachedSnapshot = currentPairing?.let(cache::load),
        )
        if (decision.unpaired) {
            observedPairing = null
            paired = false
            snapshot = null
            status = "尚未配对 Windows"
            return
        }

        observedPairing = currentPairing
        paired = true
        if (decision.pairingChanged) {
            snapshot = decision.snapshotToDisplay
            status = snapshot?.let { "上次同步于 ${formatSyncTime(it.generatedAtUtc)}" } ?: "已配对，尚未同步"
        } else if (decision.snapshotToDisplay !== snapshot && decision.snapshotToDisplay != null) {
            snapshot = decision.snapshotToDisplay
            status = "上次同步于 ${formatSyncTime(decision.snapshotToDisplay.generatedAtUtc)}"
        }
    }

    fun onHidden() { visible = false }
    fun onStart() {
        if (registered) return
        val filter = IntentFilter(TokenUsageRefreshEvents.ACTION_COMPLETED)
        ContextCompat.registerReceiver(host, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        registered = true
    }

    fun onStop() {
        if (!registered) return
        host.unregisterReceiver(receiver)
        registered = false
    }

    fun destroy() { onStop(); destroyed = true; worker.shutdownNow() }
    fun requestSync() { if (canSync) requestSync(store.load(), AutomaticRefreshReason.MANUAL) }

    private fun requestSync(pairing: TokenSyncPairing?, reason: AutomaticRefreshReason) {
        if (pairing == null || syncing || destroyed) return
        val enabled = refreshSettings.load().autoSyncOnOpen
        if (!AppAutomaticRefreshCoordinator.tryStart(AutomaticRefreshChannel.TOKEN, reason, enabled)) return
        val snapshotAtStart = snapshot
        syncing = true
        status = if (snapshot == null) "正在从 Windows 同步…" else "正在同步；当前显示缓存"
        worker.execute {
            val result = try {
                runCatching {
                    TokenUsageSyncCoordinator(host).sync(
                        pairing,
                        forceRefresh = shouldForceTokenUsageRefresh(reason),
                    )
                }
            } finally {
                AppAutomaticRefreshCoordinator.finish(AutomaticRefreshChannel.TOKEN)
            }
            main.post {
                if (destroyed) return@post
                syncing = false
                result.onSuccess { synced ->
                    snapshot = synced.snapshot
                    observedPairing = store.load() ?: synced.pairing
                    status = "上次同步于 ${formatSyncTime(synced.snapshot.generatedAtUtc)}"
                }.onFailure { error ->
                    val latestSnapshot = loadCachedSnapshot()
                    if (latestSnapshot != null && hasNewerTokenUsageSnapshot(snapshotAtStart, latestSnapshot)) {
                        snapshot = latestSnapshot
                        status = "上次同步于 ${formatSyncTime(latestSnapshot.generatedAtUtc)}"
                    } else {
                        val message = tokenUsageSyncErrorMessage(error)
                        status = snapshot?.let { "上次同步于 ${formatSyncTime(it.generatedAtUtc)} · $message" } ?: message
                    }
                }
            }
        }
    }

    private fun loadCachedSnapshot(): TokenUsageSnapshot? = store.load()?.let(cache::load)
}

/** Pure local decision used by the controller and regression tests. */
internal data class TokenPairingReconcileDecision(
    val pairingChanged: Boolean,
    val unpaired: Boolean,
    val snapshotToDisplay: TokenUsageSnapshot?,
)

internal fun tokenPairingReconcileDecision(
    previousPairing: TokenSyncPairing?,
    currentPairing: TokenSyncPairing?,
    currentSnapshot: TokenUsageSnapshot?,
    cachedSnapshot: TokenUsageSnapshot?,
): TokenPairingReconcileDecision {
    if (currentPairing == null) {
        return TokenPairingReconcileDecision(pairingChanged = previousPairing != null, unpaired = true, snapshotToDisplay = null)
    }
    val changed = previousPairing == null || !previousPairing.matchesConfiguration(currentPairing)
    if (changed) {
        return TokenPairingReconcileDecision(pairingChanged = true, unpaired = false, snapshotToDisplay = cachedSnapshot)
    }
    return TokenPairingReconcileDecision(
        pairingChanged = false,
        unpaired = false,
        snapshotToDisplay = if (cachedSnapshot != null && hasNewerTokenUsageSnapshot(currentSnapshot, cachedSnapshot)) cachedSnapshot else currentSnapshot,
    )
}

/** Avoids allowing an earlier failed foreground request to overwrite a newer pairing sync. */
internal fun hasNewerTokenUsageSnapshot(
    snapshotAtStart: TokenUsageSnapshot?,
    latestSnapshot: TokenUsageSnapshot?,
): Boolean {
    if (latestSnapshot == null) return false
    if (snapshotAtStart == null) return true
    val currentVersion = runCatching { Instant.parse(snapshotAtStart.generatedAtUtc) }.getOrNull()
    val latestVersion = runCatching { Instant.parse(latestSnapshot.generatedAtUtc) }.getOrNull()
    return if (currentVersion != null && latestVersion != null) {
        latestVersion.isAfter(currentVersion)
    } else {
        latestSnapshot.generatedAtUtc > snapshotAtStart.generatedAtUtc
    }
}

@Composable
internal fun TokenUsagePage(controller: TokenUsagePageController, onPairing: () -> Unit, modifier: Modifier = Modifier) {
    val palette = LocalQuotaPalette.current
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Token 用量", fontSize = 21.sp, fontWeight = FontWeight.Bold, color = palette.color(palette.title))
        TokenUsageStatusLine(controller.status)
        if (!controller.paired) {
            Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp), colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface))) {
                Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("Token 用量", fontSize = 19.sp, fontWeight = FontWeight.Bold)
                    Text("连接 Windows CodexQuotaTray 后，即可查看本机 Codex Token 使用历史。", color = palette.color(palette.secondary))
                    Button(onClick = rememberSystemHapticClick(onPairing), modifier = Modifier.fillMaxWidth()) { Text("扫码配对") }
                }
            }
        } else controller.snapshot?.let { TokenUsageContent(it) }
        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun TokenUsageStatusLine(status: String) {
    val palette = LocalQuotaPalette.current
    val separator = " · "
    val separatorIndex = status.indexOf(separator)
    if (separatorIndex >= 0) {
        Row {
            Text(status.substring(0, separatorIndex), fontSize = 14.sp, color = palette.color(palette.muted))
            Text(separator, fontSize = 14.sp, color = palette.color(palette.muted))
            Text(status.substring(separatorIndex + separator.length), fontSize = 14.sp, color = palette.color(palette.error))
        }
    } else {
        Text(
            status,
            fontSize = 14.sp,
            color = palette.color(
                if (status.contains("Windows 当前不可用") || status.contains("Token 同步数据保存失败")) palette.error else palette.muted,
            ),
        )
    }

}

@Composable
private fun TokenUsageContent(snapshot: TokenUsageSnapshot) {
    val palette = LocalQuotaPalette.current
    val first = listOf("今日 Token" to snapshot.summary.todayTokens, "7 天 Token" to snapshot.summary.last7DaysTokens, "30 天 Token" to snapshot.summary.last30DaysTokens, "累计 Token" to snapshot.summary.lifetimeTokens)
    val second = listOf("峰值 Token" to snapshot.summary.peakDailyTokens, "当前连续天数" to snapshot.summary.currentStreak.toLong(), "最长连续天数" to snapshot.summary.longestStreak.toLong())
    SummaryRow(first)
    SummaryRow(second)
    Text("Token 热力图", fontSize = 21.sp, fontWeight = FontWeight.Bold, color = palette.color(palette.title), modifier = Modifier.padding(top = 10.dp))
    var selected by androidx.compose.runtime.remember { mutableStateOf<TokenUsageDay?>(null) }
    Text(
        selected?.let(::formatHeatmapSelection) ?: "触摸方格查看当日用量",
        color = palette.color(palette.secondary),
        fontSize = 14.sp,
    )
    TokenHeatmap(snapshot.days) { selected = it }
}

@Composable
private fun SummaryRow(items: List<Pair<String, Long>>) {
    val palette = LocalQuotaPalette.current
    Row(Modifier.fillMaxWidth()) {
        items.forEach { (label, value) ->
            Column(Modifier.weight(1f).padding(vertical = 10.dp, horizontal = 3.dp)) {
                Text(TokenFormatter.format(value), Modifier.fillMaxWidth(), color = palette.color(palette.title), fontSize = 18.sp, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
                Text(label, Modifier.fillMaxWidth(), color = palette.color(palette.muted), fontSize = 11.sp, textAlign = TextAlign.Center)
            }
        }
    }
}

@Composable
private fun TokenHeatmap(days: List<TokenUsageDay>, selected: (TokenUsageDay) -> Unit) {
    val palette = LocalQuotaPalette.current
    val hapticFeedback = LocalHapticFeedback.current
    val range = tokenHeatmapRange(days)
    val values = days.associateBy { it.date }
    val nonZero = days.map { it.totalTokens }.filter { it > 0L }
    val colors = listOf(palette.color(palette.progressTrack), androidx.compose.ui.graphics.Color(0xffc6e48b), androidx.compose.ui.graphics.Color(0xff7bc96f), androidx.compose.ui.graphics.Color(0xff239a3b), androidx.compose.ui.graphics.Color(0xff196127))
    val scroll = rememberScrollState(initial = Int.MAX_VALUE)
    Canvas(
        Modifier
            .horizontalScroll(scroll)
            .size(width = (range.columnCount * 18 - 3).dp, height = 126.dp)
            .pointerInput(range, days) {
            detectTapGestures { point ->
                val column = (point.x / 18.dp.toPx()).toInt(); val row = (point.y / 18.dp.toPx()).toInt(); val index = column * 7 + row
                if (index in 0 until range.dayCount) {
                    hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                    val date = range.start.plusDays(index.toLong())
                    selected(values[date] ?: TokenUsageDay(date, 0, null, null, null, null))
                }
            }
        },
    ) {
        val cell = 15.dp.toPx(); val gap = 3.dp.toPx()
        repeat(range.dayCount) { index ->
            val date = range.start.plusDays(index.toLong()); val tokens = values[date]?.totalTokens ?: 0L
            drawRoundRect(colors[HeatmapBuckets.bucket(tokens, nonZero)], Offset((index / 7) * (cell + gap), (index % 7) * (cell + gap)), Size(cell, cell), androidx.compose.ui.geometry.CornerRadius(2.dp.toPx()))
        }
    }
}

internal data class TokenHeatmapRange(
    val start: LocalDate,
    val end: LocalDate,
) {
    val dayCount: Int get() = ChronoUnit.DAYS.between(start, end).toInt() + 1
    val columnCount: Int get() = (dayCount + 6) / 7
}

internal fun tokenHeatmapRange(
    days: List<TokenUsageDay>,
    today: LocalDate = LocalDate.now(),
): TokenHeatmapRange {
    val recentEightWeeksStart = startOfWeek(today).minusWeeks(7)
    val firstUsageWeek = days
        .asSequence()
        .filter { it.date <= today && it.totalTokens > 0L }
        .map { startOfWeek(it.date) }
        .minOrNull()
    val desiredStart = firstUsageWeek?.let { minOf(it, recentEightWeeksStart) } ?: recentEightWeeksStart
    val oldestAllowedWeek = today.minusDays(MAX_HEATMAP_DAYS - 1L)
        .with(TemporalAdjusters.nextOrSame(DayOfWeek.SUNDAY))
    return TokenHeatmapRange(maxOf(desiredStart, oldestAllowedWeek), today)
}

internal fun formatHeatmapSelection(
    day: TokenUsageDay,
    currentYear: Int = LocalDate.now().year,
): String {
    val datePrefix = if (day.date.year == currentYear) {
        "${day.date.monthValue} 月 ${day.date.dayOfMonth} 日"
    } else {
        "${day.date.year} 年 ${day.date.monthValue} 月 ${day.date.dayOfMonth} 日"
    }
    return "$datePrefix  ${String.format(Locale.US, "%,d", day.totalTokens)} Token"
}

private fun startOfWeek(date: LocalDate): LocalDate =
    date.with(TemporalAdjusters.previousOrSame(DayOfWeek.SUNDAY))

private const val MAX_HEATMAP_DAYS = 365

private fun formatSyncTime(raw: String) = runCatching { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw))) }.getOrDefault("未知")
