package com.codexquotatray.android

import android.content.Intent
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.shadow.Shadow
import androidx.compose.ui.draw.dropShadow
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalDensity
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
import com.kyant.backdrop.highlight.Highlight

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
) {
    val palette = LocalQuotaPalette.current
    val density = LocalDensity.current
    val hapticFeedback = LocalHapticFeedback.current
    val today = LocalDate.now()
    val range = remember(today) { tokenHeatmapRange(today) }
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

    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val viewportWidthPx = with(density) { maxWidth.toPx() }
        val geometry = remember(range.start, range.dayCount, maxWidth, density) {
            val gapPx = with(density) { HEATMAP_GAP.toPx() }
            val maxCellSizePx = with(density) { HEATMAP_MAX_CELL_SIZE.toPx() }
            val cellSizePx = ((viewportWidthPx - (TOKEN_HEATMAP_COLUMNS - 1) * gapPx) / TOKEN_HEATMAP_COLUMNS)
                .coerceAtLeast(1f)
                .coerceAtMost(maxCellSizePx)
            val contentWidthPx = TOKEN_HEATMAP_COLUMNS * cellSizePx + (TOKEN_HEATMAP_COLUMNS - 1) * gapPx
            HeatmapGeometry(
                cellSizePx = cellSizePx,
                gapPx = gapPx,
                startDate = range.start,
                dayCount = range.dayCount,
                contentOffsetX = centeredHeatmapOffset(viewportWidthPx, contentWidthPx),
            )
        }
        val gridWidth = with(density) { geometry.contentWidthPx.toDp() }
        val gridHeight = with(density) { geometry.contentHeightPx.toDp() }
        val gridCellSize = with(density) { geometry.cellSizePx.toDp() }
        val selectedIndex = selectedDate?.let { ChronoUnit.DAYS.between(range.start, it).toInt() }
        val selectedBounds = selectedIndex?.let(geometry::cellBounds)
        val selectedDay = if (selectedDate != null && selectedBounds != null) {
            values[selectedDate] ?: TokenUsageDay(selectedDate, 0, null, null, null, null)
        } else {
            null
        }
        val tooltipWidthPx = with(density) { HEATMAP_TOOLTIP_WIDTH.toPx() }
        val tooltipHeightPx = with(density) { HEATMAP_TOOLTIP_HEIGHT.toPx() }
        val tooltipClearancePx = with(density) { HEATMAP_TOOLTIP_CLEARANCE.toPx() }
        val containerHeight = gridHeight
        val containerHeightPx = with(density) { gridHeight.toPx() }
        val tooltipPlacement = if (selectedBounds != null) {
            placeHeatmapTooltip(
                viewportWidthPx = viewportWidthPx,
                containerHeightPx = containerHeightPx,
                cellBounds = selectedBounds,
                tooltipWidthPx = tooltipWidthPx,
                tooltipHeightPx = tooltipHeightPx,
                selectedScale = HEATMAP_SELECTED_SCALE,
                clearancePx = tooltipClearancePx,
            )
        } else {
            null
        }
        val heatmapBackdrop = rememberLayerBackdrop()

        Box(
            Modifier
                .fillMaxWidth()
                .height(containerHeight)
                .semantics {
                    contentDescription = selectedDay?.let {
                        "${formatHeatmapTooltipDate(it.date)}，${formatHeatmapTooltipTokenCount(it.totalTokens)}"
                    } ?: "Token 使用热力图"
                }
                .pointerInput(geometry) {
                    var scrubbedDate: LocalDate? = null
                    detectTokenHeatmapGestures(
                        onSelectionStart = { point ->
                            val index = geometry.hitTest(
                                point = point,
                            )
                            val date = index?.let(geometry::indexToDate)
                            if (date == null) {
                                false
                            } else {
                                scrubbedDate = date
                                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                                onSelected(date)
                                true
                            }
                        },
                        onSelectionMove = { point ->
                            val index = geometry.hitTest(
                                point = point,
                            )
                            val date = index?.let(geometry::indexToDate)
                            val nextDate = heatmapSelectionAfterHit(scrubbedDate, date)
                            if (nextDate != null && nextDate != scrubbedDate) {
                                scrubbedDate = nextDate
                                onSelected(nextDate)
                            }
                        },
                        onSelectionEnd = {
                            scrubbedDate = null
                        },
                    )
                },
        ) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(gridHeight)
                    .layerBackdrop(heatmapBackdrop),
            ) {
                Box(
                    Modifier
                        .width(gridWidth)
                        .height(gridHeight)
                        .align(androidx.compose.ui.Alignment.Center),
                ) {
                    Canvas(Modifier.fillMaxSize()) {
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

            if (selectedDay != null && selectedBounds != null) {
                val selectedColor = colors[HeatmapBuckets.bucket(selectedDay.totalTokens, nonZero)]
                HeatmapSelectedCell(
                    color = selectedColor,
                    cellSize = gridCellSize,
                    modifier = Modifier
                        .offset {
                            IntOffset(
                                selectedBounds.left.roundToInt(),
                                selectedBounds.top.roundToInt(),
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
    cellSize: androidx.compose.ui.unit.Dp,
    modifier: Modifier = Modifier,
) {
    val scale = remember { Animatable(1f) }
    LaunchedEffect(Unit) {
        scale.snapTo(1f)
        scale.animateTo(HEATMAP_SELECTED_SCALE, tween(170))
    }
    val shape = RoundedCornerShape(HEATMAP_CORNER_RADIUS)
    val edgeColor = lerp(color, Color.White, 0.24f)
    Box(
        modifier
            .size(cellSize)
            .graphicsLayer {
                scaleX = scale.value
                scaleY = scale.value
                transformOrigin = TransformOrigin.Center
            }
            .dropShadow(
                shape = shape,
                shadow = Shadow(
                    radius = 11.dp,
                    spread = 2.dp,
                    color = color.copy(alpha = 0.6f),
                    offset = DpOffset.Zero,
                ),
            )
            .background(color, shape)
            .border(1.dp, edgeColor, shape),
    )
}

@Composable
private fun HeatmapGlassTooltip(
    day: TokenUsageDay,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    val tooltipSurface = if (palette.color(palette.background).luminance() < 0.35f) {
        Color(0xff121212)
    } else {
        null
    }
    val scale = remember { Animatable(0.96f) }
    LaunchedEffect(Unit) {
        scale.animateTo(1f, tween(160))
    }
    GlassSurface(
        backdrop = backdrop,
        shape = RoundedCornerShape(16.dp),
        blurRadius = 8.dp,
        refractionHeight = 24.dp,
        refractionAmount = 48.dp,
        lensDepthEffect = true,
        enableColorControls = true,
        saturation = 1.5f,
        highlight = Highlight.Plain,
        surfaceAlpha = 0.4f,
        surfaceColor = tooltipSurface,
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
    val columnCount: Int get() = TOKEN_HEATMAP_COLUMNS
}

internal fun tokenHeatmapRange(
    today: LocalDate = LocalDate.now(),
): TokenHeatmapRange {
    return TokenHeatmapRange(startOfWeek(today).minusWeeks(12), today)
}

internal fun formatHeatmapTooltipTokenCount(totalTokens: Long): String =
    String.format(Locale.US, "%,d Token", totalTokens)

internal fun formatHeatmapTooltipDate(date: LocalDate): String = date.toString()

internal fun formatHeatmapSelection(day: TokenUsageDay): String =
    "${formatHeatmapTooltipTokenCount(day.totalTokens)}\n${formatHeatmapTooltipDate(day.date)}"

private fun startOfWeek(date: LocalDate): LocalDate =
    date.with(TemporalAdjusters.previousOrSame(DayOfWeek.SUNDAY))

private val HEATMAP_GAP = 5.dp
private val HEATMAP_MAX_CELL_SIZE = 24.dp
private val HEATMAP_CORNER_RADIUS = 3.dp
private val HEATMAP_TOOLTIP_WIDTH = 220.dp
private val HEATMAP_TOOLTIP_HEIGHT = 64.dp
private val HEATMAP_TOOLTIP_CLEARANCE = 24.dp
internal const val HEATMAP_SELECTED_SCALE = 1.5f

private fun formatSyncTime(raw: String) = runCatching { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw))) }.getOrDefault("未知")
