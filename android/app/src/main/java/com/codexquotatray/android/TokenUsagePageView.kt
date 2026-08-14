package com.codexquotatray.android

import android.content.Intent
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.shadow.Shadow
import androidx.compose.ui.draw.dropShadow
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalViewConfiguration
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.DpOffset
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
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
import kotlin.math.roundToInt
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

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
    var status by mutableStateOf(RefreshStatusFormatter.tokenUnpaired())
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
            status = RefreshStatusFormatter.tokenUnpaired()
            return
        }

        observedPairing = currentPairing
        paired = true
        if (decision.pairingChanged) {
            snapshot = decision.snapshotToDisplay
            status = snapshot?.let { RefreshStatusFormatter.loaded("Windows", formatSyncTime(it.generatedAtUtc)) }
                ?: RefreshStatusFormatter.tokenPairedWithoutData()
        } else if (decision.snapshotToDisplay !== snapshot && decision.snapshotToDisplay != null) {
            snapshot = decision.snapshotToDisplay
            status = RefreshStatusFormatter.loaded("Windows", formatSyncTime(decision.snapshotToDisplay.generatedAtUtc))
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
        status = RefreshStatusFormatter.refreshing(snapshot != null)
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
                    status = RefreshStatusFormatter.loaded("Windows", formatSyncTime(synced.snapshot.generatedAtUtc))
                }.onFailure { error ->
                    val latestSnapshot = loadCachedSnapshot()
                    if (latestSnapshot != null && hasNewerTokenUsageSnapshot(snapshotAtStart, latestSnapshot)) {
                        snapshot = latestSnapshot
                        status = RefreshStatusFormatter.loaded("Windows", formatSyncTime(latestSnapshot.generatedAtUtc))
                    } else {
                        val message = tokenUsageSyncErrorMessage(error)
                        status = RefreshStatusFormatter.failure(
                            reason = message,
                            updatedAt = snapshot?.let { formatSyncTime(it.generatedAtUtc) },
                        )
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
    RefreshStatusLine(status)
}

@Composable
private fun TokenUsageContent(snapshot: TokenUsageSnapshot) {
    val first = listOf("今日 Token" to snapshot.summary.todayTokens, "7 天 Token" to snapshot.summary.last7DaysTokens, "30 天 Token" to snapshot.summary.last30DaysTokens, "累计 Token" to snapshot.summary.lifetimeTokens)
    val second = listOf("峰值 Token" to snapshot.summary.peakDailyTokens, "当前连续天数" to snapshot.summary.currentStreak.toLong(), "最长连续天数" to snapshot.summary.longestStreak.toLong())
    SummaryRow(first)
    SummaryRow(second)
    Spacer(Modifier.height(16.dp))
    var selectedDate by remember { mutableStateOf<LocalDate?>(null) }
    TokenHeatmap(
        days = snapshot.days,
        selectedDate = selectedDate,
        onSelected = { selectedDate = it },
        onClearSelection = { selectedDate = null },
    )
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
private fun TokenHeatmap(
    days: List<TokenUsageDay>,
    selectedDate: LocalDate?,
    onSelected: (LocalDate) -> Unit,
    onClearSelection: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val density = LocalDensity.current
    val hapticFeedback = LocalHapticFeedback.current
    val viewConfiguration = LocalViewConfiguration.current
    val range = remember(days) { tokenHeatmapRange(days) }
    val values = remember(days) { days.associateBy { it.date } }
    val nonZero = remember(days) { days.map { it.totalTokens }.filter { it > 0L } }
    val colors = remember(palette) {
        listOf(
            palette.color(palette.progressTrack),
            androidx.compose.ui.graphics.Color(0xffc6e48b),
            androidx.compose.ui.graphics.Color(0xff7bc96f),
            androidx.compose.ui.graphics.Color(0xff239a3b),
            androidx.compose.ui.graphics.Color(0xff196127),
        )
    }
    val scroll = rememberScrollState(initial = Int.MAX_VALUE)
    var scrubbing by remember { mutableStateOf(false) }

    LaunchedEffect(scroll) {
        snapshotFlow { scroll.isScrollInProgress to scrubbing }.collect { (scrolling, activeScrub) ->
            if (scrolling && !activeScrub) onClearSelection()
        }
    }

    LaunchedEffect(range.start, range.end) {
        if (selectedDate != null && selectedDate !in range.start..range.end) onClearSelection()
    }

    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val viewportWidthPx = with(density) { maxWidth.toPx() }
        val geometry = remember(range.start, range.end, maxWidth, density) {
            val cellSizePx = with(density) { HEATMAP_CELL_SIZE.toPx() }
            val gapPx = with(density) { HEATMAP_GAP.toPx() }
            val contentWidthPx = if (range.columnCount == 0) 0f else {
                range.columnCount * cellSizePx + (range.columnCount - 1) * gapPx
            }
            HeatmapGeometry(
                cellSizePx = cellSizePx,
                gapPx = gapPx,
                columnCount = range.columnCount,
                startDate = range.start,
                dayCount = range.dayCount,
                contentOffsetX = centeredHeatmapOffset(viewportWidthPx, contentWidthPx),
            )
        }
        val gridWidth = with(density) { geometry.contentWidthPx.toDp() }
        val gridHeight = with(density) { geometry.contentHeightPx.toDp() }
        val scrollContentWidth = maxOf(maxWidth, gridWidth)
        val selectedIndex = selectedDate?.let { ChronoUnit.DAYS.between(range.start, it).toInt() }
        val selectedBounds = selectedIndex?.let(geometry::cellBounds)
        val selectedDay = if (selectedDate != null && selectedBounds != null) {
            values[selectedDate] ?: TokenUsageDay(selectedDate, 0, null, null, null, null)
        } else {
            null
        }
        val tooltipVisible = selectedDay != null
        val tooltipReserve = if (tooltipVisible) HEATMAP_TOOLTIP_RESERVE else 0.dp
        val tooltipWidthPx = with(density) { HEATMAP_TOOLTIP_WIDTH.toPx() }
        val tooltipHeightPx = with(density) { HEATMAP_TOOLTIP_HEIGHT.toPx() }
        val tooltipGapPx = with(density) { HEATMAP_TOOLTIP_GAP.toPx() }
        val containerHeight = tooltipReserve + gridHeight
        val containerHeightPx = with(density) { containerHeight.toPx() }
        val tooltipPlacement = if (selectedBounds != null) {
            placeHeatmapTooltip(
                viewportWidthPx = viewportWidthPx,
                containerHeightPx = containerHeightPx,
                cellBounds = selectedBounds,
                horizontalScrollPx = scroll.value.toFloat(),
                tooltipWidthPx = tooltipWidthPx,
                tooltipHeightPx = tooltipHeightPx,
                topReservePx = with(density) { tooltipReserve.toPx() },
                gapPx = tooltipGapPx,
            )
        } else {
            null
        }
        val heatmapBackdrop = rememberLayerBackdrop()
        val reservePx = with(density) { tooltipReserve.toPx() }

        Box(
            Modifier
                .fillMaxWidth()
                .height(containerHeight)
                .semantics {
                    contentDescription = selectedDay?.let {
                        "${formatHeatmapTooltipDate(it.date)}，${formatHeatmapTooltipTokenCount(it.totalTokens)}"
                    } ?: "Token 使用热力图"
                }
                .pointerInput(geometry, reservePx, scroll) {
                    var scrubbedDate: LocalDate? = null
                    detectTokenHeatmapGestures(
                        touchSlop = viewConfiguration.touchSlop,
                        longPressTimeoutMillis = viewConfiguration.longPressTimeoutMillis,
                        onTap = { point ->
                            val index = geometry.hitTest(
                                point = Offset(point.x, point.y - reservePx),
                                horizontalScrollPx = scroll.value.toFloat(),
                            )
                            val date = index?.let(geometry::indexToDate)
                            if (date == null) {
                                onClearSelection()
                            } else {
                                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                                onSelected(date)
                            }
                        },
                        onScrubStart = { point ->
                            val index = geometry.hitTest(
                                point = Offset(point.x, point.y - reservePx),
                                horizontalScrollPx = scroll.value.toFloat(),
                            )
                            val date = index?.let(geometry::indexToDate)
                            if (date == null) {
                                false
                            } else {
                                scrubbedDate = date
                                scrubbing = true
                                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                                onSelected(date)
                                true
                            }
                        },
                        onScrubMove = { point ->
                            val index = geometry.hitTest(
                                point = Offset(point.x, point.y - reservePx),
                                horizontalScrollPx = scroll.value.toFloat(),
                            )
                            val date = index?.let(geometry::indexToDate)
                            if (date != null && date != scrubbedDate) {
                                scrubbedDate = date
                                onSelected(date)
                            }
                        },
                        onScrubEnd = {
                            scrubbedDate = null
                            scrubbing = false
                        },
                    )
                },
        ) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(gridHeight)
                    .offset(y = tooltipReserve)
                    .layerBackdrop(heatmapBackdrop),
            ) {
                Box(
                    Modifier
                        .fillMaxWidth()
                        .horizontalScroll(scroll),
                ) {
                    Box(Modifier.width(scrollContentWidth).height(gridHeight)) {
                        Canvas(
                            Modifier
                                .offset(x = with(density) { geometry.contentOffsetX.toDp() })
                                .size(gridWidth, gridHeight),
                        ) {
                            repeat(range.dayCount) { index ->
                                val date = range.start.plusDays(index.toLong())
                                val tokens = values[date]?.totalTokens ?: 0L
                                val column = index / geometry.rowCount
                                val row = index % geometry.rowCount
                                drawRoundRect(
                                    color = colors[HeatmapBuckets.bucket(tokens, nonZero)],
                                    topLeft = Offset(column * geometry.stridePx, row * geometry.stridePx),
                                    size = Size(geometry.cellSizePx, geometry.cellSizePx),
                                    cornerRadius = androidx.compose.ui.geometry.CornerRadius(
                                        with(density) { HEATMAP_CORNER_RADIUS.toPx() },
                                    ),
                                )
                            }
                        }
                    }
                }
            }

            if (selectedDay != null && selectedBounds != null) {
                val selectedColor = colors[HeatmapBuckets.bucket(selectedDay.totalTokens, nonZero)]
                HeatmapSelectedCell(
                    color = selectedColor,
                    animate = !scrubbing,
                    modifier = Modifier
                        .offset {
                            IntOffset(
                                (selectedBounds.left - scroll.value).roundToInt(),
                                (selectedBounds.top + reservePx).roundToInt(),
                            )
                        }
                        .zIndex(1f),
                )
                tooltipPlacement?.let { placement ->
                    HeatmapGlassTooltip(
                        day = selectedDay,
                        backdrop = heatmapBackdrop,
                        modifier = Modifier
                            .offset {
                                IntOffset(placement.x.roundToInt(), placement.y.roundToInt())
                            }
                            .zIndex(2f),
                    )
                }
            }
        }
    }
}

@Composable
private fun HeatmapSelectedCell(
    color: androidx.compose.ui.graphics.Color,
    animate: Boolean,
    modifier: Modifier = Modifier,
) {
    val scale = remember { Animatable(1f) }
    LaunchedEffect(Unit) {
        if (animate) {
            scale.snapTo(1f)
            scale.animateTo(HEATMAP_SELECTED_SCALE, tween(140))
        } else {
            scale.snapTo(HEATMAP_SELECTED_SCALE)
        }
    }
    val shape = RoundedCornerShape(HEATMAP_CORNER_RADIUS)
    Box(
        modifier
            .size(HEATMAP_CELL_SIZE)
            .graphicsLayer {
                scaleX = scale.value
                scaleY = scale.value
                transformOrigin = TransformOrigin.Center
            }
            .dropShadow(
                shape = shape,
                shadow = Shadow(
                    radius = 8.dp,
                    spread = 1.dp,
                    color = color.copy(alpha = 0.4f),
                    offset = DpOffset.Zero,
                ),
            )
            .background(color, shape),
    )
}

@Composable
private fun HeatmapGlassTooltip(
    day: TokenUsageDay,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    val scale = remember { Animatable(0.96f) }
    LaunchedEffect(Unit) {
        scale.animateTo(1f, tween(160))
    }
    GlassSurface(
        backdrop = backdrop,
        shape = RoundedCornerShape(16.dp),
        modifier = modifier
            .width(HEATMAP_TOOLTIP_WIDTH)
            .height(HEATMAP_TOOLTIP_HEIGHT)
            .graphicsLayer {
                scaleX = scale.value
                scaleY = scale.value
                transformOrigin = TransformOrigin.Center
            }
            .semantics {
                contentDescription = "${formatHeatmapTooltipDate(day.date)}，${formatHeatmapTooltipTokenCount(day.totalTokens)}"
            },
    ) {
        Column(
            Modifier.padding(horizontal = 16.dp, vertical = 10.dp),
            verticalArrangement = Arrangement.spacedBy(1.dp),
        ) {
            Text(
                formatHeatmapTooltipTokenCount(day.totalTokens),
                color = palette.color(palette.title),
                fontSize = 18.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
            )
            Text(
                formatHeatmapTooltipDate(day.date),
                color = palette.color(palette.muted),
                fontSize = 14.sp,
                maxLines = 1,
            )
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

internal fun formatHeatmapTooltipTokenCount(totalTokens: Long): String =
    String.format(Locale.US, "%,d Token", totalTokens)

internal fun formatHeatmapTooltipDate(date: LocalDate): String = date.toString()

internal fun formatHeatmapSelection(day: TokenUsageDay): String =
    "${formatHeatmapTooltipTokenCount(day.totalTokens)}\n${formatHeatmapTooltipDate(day.date)}"

private fun startOfWeek(date: LocalDate): LocalDate =
    date.with(TemporalAdjusters.previousOrSame(DayOfWeek.SUNDAY))

private val HEATMAP_CELL_SIZE = 18.dp
private val HEATMAP_GAP = 4.dp
private val HEATMAP_CORNER_RADIUS = 3.dp
private val HEATMAP_TOOLTIP_RESERVE = 72.dp
private val HEATMAP_TOOLTIP_WIDTH = 220.dp
private val HEATMAP_TOOLTIP_HEIGHT = 64.dp
private val HEATMAP_TOOLTIP_GAP = 8.dp
private const val HEATMAP_SELECTED_SCALE = 1.15f
private const val MAX_HEATMAP_DAYS = 365

private fun formatSyncTime(raw: String) = runCatching { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw))) }.getOrDefault("未知")
