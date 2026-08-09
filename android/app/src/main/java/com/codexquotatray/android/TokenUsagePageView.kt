package com.codexquotatray.android

import android.content.Intent
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
import com.codexquotatray.android.usage.HeatmapBuckets
import com.codexquotatray.android.usage.TokenFormatter
import com.codexquotatray.android.usage.TokenSyncEndpoint
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenUsageCache
import com.codexquotatray.android.usage.TokenUsageDay
import com.codexquotatray.android.usage.TokenUsageException
import com.codexquotatray.android.usage.TokenUsageRefreshSettingsStore
import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSyncClient
import java.text.SimpleDateFormat
import java.time.Instant
import java.time.LocalDate
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

internal class TokenUsagePageController(private val host: MainActivity) {
    private val cache by lazy { TokenUsageCache(host) }
    private val store by lazy { TokenSyncStore(host) }
    private val refreshSettings by lazy { TokenUsageRefreshSettingsStore(host) }
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var pairingKey: String? = null
    private var pairingLoaded = false
    private var destroyed = false
    private var visible = false
    var syncing by mutableStateOf(false)
        private set
    var snapshot by mutableStateOf<TokenUsageSnapshot?>(null)
        private set
    var status by mutableStateOf("尚未打开统计")
        private set
    var paired by mutableStateOf(false)
        private set

    val canSync get() = !syncing && store.load() != null

    fun onVisible() {
        visible = true
        val pairing = store.load()
        val autoSyncOnOpen = refreshSettings.load().autoSyncOnOpen
        val key = pairing?.let { "${it.deviceId}|${it.pairingSecret}|$autoSyncOnOpen" }
        if (pairingLoaded && key == pairingKey) return
        pairingLoaded = true; pairingKey = key; paired = pairing != null
        if (pairing == null) { snapshot = null; status = "尚未配对 Windows"; return }
        cache.load()?.let { snapshot = it; status = "上次同步于 ${formatSyncTime(it.generatedAtUtc)}" } ?: run { status = "暂无 Token 使用量缓存" }
        if (autoSyncOnOpen) sync(pairing) else if (snapshot == null) status = "自动同步已关闭"
    }

    fun onResume() { if (visible) onVisible() }
    fun onHidden() { visible = false }
    fun destroy() { destroyed = true; worker.shutdownNow() }
    fun requestSync() { if (canSync) sync(store.load()) }

    private fun sync(pairing: TokenSyncPairing?) {
        if (pairing == null || syncing || destroyed) return
        syncing = true
        status = if (snapshot == null) "正在从 Windows 同步…" else "正在同步；当前显示缓存"
        worker.execute {
            val result = runCatching { TokenUsageSyncClient(host).sync(pairing) }
            main.post {
                if (destroyed) return@post
                syncing = false
                result.onSuccess { synced ->
                    store.save(TokenSyncEndpoint.markSynced(synced.pairing, synced.snapshot))
                    cache.save(synced.snapshot)
                    snapshot = synced.snapshot
                    status = "上次同步于 ${formatSyncTime(synced.snapshot.generatedAtUtc)}"
                }.onFailure { error ->
                    val message = (error as? TokenUsageException)?.message ?: "Windows 当前不可用"
                    status = snapshot?.let { "上次同步于 ${formatSyncTime(it.generatedAtUtc)} · $message" } ?: message
                }
            }
        }
    }
}

@Composable
internal fun TokenUsagePage(controller: TokenUsagePageController, onSettings: () -> Unit, modifier: Modifier = Modifier) {
    val palette = LocalQuotaPalette.current
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Token 用量", fontSize = 21.sp, fontWeight = FontWeight.Bold, color = palette.color(palette.title))
        TokenUsageStatusLine(controller.status)
        if (!controller.paired) {
            Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp), colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface))) {
                Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("Token 用量", fontSize = 19.sp, fontWeight = FontWeight.Bold)
                    Text("连接 Windows CodexQuotaTray 后，即可查看本机 Codex Token 使用历史。", color = palette.color(palette.secondary))
                    Button(onClick = rememberSystemHapticClick(onSettings), modifier = Modifier.fillMaxWidth()) { Text("前往设置") }
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
            color = palette.color(if (status.contains("Windows 当前不可用")) palette.error else palette.muted),
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
    Text(selected?.let { "${it.date.monthValue} 月 ${it.date.dayOfMonth} 日  ${String.format(Locale.US, "%,d", it.totalTokens)} Token" } ?: "触摸方格查看当日用量", color = palette.color(palette.secondary), fontSize = 14.sp)
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
    val start = LocalDate.now().minusDays(364)
    val values = days.associateBy { it.date }
    val nonZero = days.map { it.totalTokens }.filter { it > 0L }
    val colors = listOf(palette.color(palette.progressTrack), androidx.compose.ui.graphics.Color(0xffc6e48b), androidx.compose.ui.graphics.Color(0xff7bc96f), androidx.compose.ui.graphics.Color(0xff239a3b), androidx.compose.ui.graphics.Color(0xff196127))
    val scroll = rememberScrollState(initial = Int.MAX_VALUE)
    Canvas(
        Modifier.horizontalScroll(scroll).size(width = 954.dp, height = 126.dp).pointerInput(days) {
            detectTapGestures { point ->
                val column = (point.x / 18.dp.toPx()).toInt(); val row = (point.y / 18.dp.toPx()).toInt(); val index = column * 7 + row
                if (index in 0 until 365) {
                    hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                    val date = start.plusDays(index.toLong())
                    selected(values[date] ?: TokenUsageDay(date, 0, null, null, null, null))
                }
            }
        },
    ) {
        val cell = 15.dp.toPx(); val gap = 3.dp.toPx()
        repeat(365) { index ->
            val date = start.plusDays(index.toLong()); val tokens = values[date]?.totalTokens ?: 0L
            drawRoundRect(colors[HeatmapBuckets.bucket(tokens, nonZero)], Offset((index / 7) * (cell + gap), (index % 7) * (cell + gap)), Size(cell, cell), androidx.compose.ui.geometry.CornerRadius(2.dp.toPx()))
        }
    }
}

private fun formatSyncTime(raw: String) = runCatching { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw))) }.getOrDefault("未知")
