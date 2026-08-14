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
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.VisibilityThreshold
import androidx.compose.animation.core.spring
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
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.shadow.Shadow
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.dropShadow
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInParent
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
import dev.chrisbanes.haze.HazeState
import dev.chrisbanes.haze.hazeEffect
import dev.chrisbanes.haze.hazeSource
import dev.chrisbanes.haze.rememberHazeState
import dev.chrisbanes.haze.blur.HazeColorEffect
import dev.chrisbanes.haze.blur.blurEffect

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
    val tokenContentHazeState = rememberHazeState()
    var selectedDate by remember { mutableStateOf<LocalDate?>(null) }
    var tooltipPresentation by remember { mutableStateOf<HeatmapTooltipPresentation?>(null) }
    val tooltipTarget = tooltipPresentation?.target
    val tooltipPositionAnimation = remember {
        Animatable(
            initialValue = Offset.Zero,
            typeConverter = Offset.VectorConverter,
            visibilityThreshold = Offset.VisibilityThreshold,
        )
    }
    var tooltipPositionInitialized by remember { mutableStateOf(false) }
    LaunchedEffect(tooltipTarget) {
        if (tooltipTarget == null) {
            tooltipPositionInitialized = false
        } else if (!tooltipPositionInitialized) {
            tooltipPositionAnimation.snapTo(tooltipTarget)
            tooltipPositionInitialized = true
        } else {
            tooltipPositionAnimation.animateTo(
                targetValue = tooltipTarget,
                animationSpec = spring(
                    dampingRatio = 0.5f,
                    stiffness = 300f,
                    visibilityThreshold = Offset.VisibilityThreshold,
                ),
            )
        }
    }
    val tooltipOffset = if (tooltipTarget != null && !tooltipPositionInitialized) {
        tooltipTarget
    } else {
        tooltipPositionAnimation.value
    }
    Box(Modifier.fillMaxWidth()) {
        Column(
            Modifier
                .fillMaxWidth()
                .hazeSource(tokenContentHazeState),
        ) {
            SummaryRow(first)
            SummaryRow(second)
            Spacer(Modifier.height(16.dp))
            TokenHeatmap(
                days = snapshot.days,
                selectedDate = selectedDate,
                onSelected = { selectedDate = it },
                onClearSelection = { selectedDate = null },
                onTooltipChanged = { tooltipPresentation = it },
            )
        }
        tooltipPresentation?.let { presentation ->
            HeatmapBlurTooltip(
                day = presentation.day,
                hazeState = tokenContentHazeState,
                modifier = Modifier
                    .offset {
                        IntOffset(tooltipOffset.x.roundToInt(), tooltipOffset.y.roundToInt())
                    }
                    .zIndex(2f),
            )
        }
    }
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

private data class HeatmapVisualSelection(
    val date: LocalDate,
    val bounds: Rect,
    val color: Color,
)

private data class HeatmapTooltipPresentation(
    val day: TokenUsageDay,
    val target: Offset,
)

@Composable
private fun TokenHeatmap(
    days: List<TokenUsageDay>,
    selectedDate: LocalDate?,
    onSelected: (LocalDate) -> Unit,
    onClearSelection: () -> Unit,
    onTooltipChanged: (HeatmapTooltipPresentation?) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val density = LocalDensity.current
    val hapticFeedback = LocalHapticFeedback.current
    val latestSelectedDate = rememberUpdatedState(selectedDate)
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

    var heatmapOriginInParent by remember { mutableStateOf(Offset.Zero) }
    BoxWithConstraints(
        Modifier
            .fillMaxWidth()
            .onGloballyPositioned { coordinates ->
                heatmapOriginInParent = coordinates.positionInParent()
            },
    ) {
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
        val currentVisualSelection = if (selectedDate != null && selectedBounds != null && selectedDay != null) {
            HeatmapVisualSelection(
                date = selectedDate,
                bounds = selectedBounds,
                color = colors[HeatmapBuckets.bucket(selectedDay.totalTokens, nonZero)],
            )
        } else {
            null
        }
        var visualSelection by remember { mutableStateOf<HeatmapVisualSelection?>(null) }
        val selectedScaleAnimation = remember {
            Animatable(1f)
        }
        LaunchedEffect(currentVisualSelection) {
            if (currentVisualSelection != null) {
                val wasVisible = visualSelection != null
                visualSelection = currentVisualSelection
                if (!wasVisible) {
                    selectedScaleAnimation.snapTo(1f)
                }
                selectedScaleAnimation.animateTo(
                    targetValue = HEATMAP_SELECTED_SCALE,
                    animationSpec = tween(170),
                )
            } else if (visualSelection != null) {
                selectedScaleAnimation.animateTo(
                    targetValue = 1f,
                    animationSpec = tween(170),
                )
                visualSelection = null
            }
        }
        val renderedVisualSelection = currentVisualSelection ?: visualSelection
        val tooltipWidthPx = with(density) { HEATMAP_TOOLTIP_WIDTH.toPx() }
        val tooltipHeightPx = with(density) { HEATMAP_TOOLTIP_HEIGHT.toPx() }
        val tooltipClearancePx = with(density) { HEATMAP_TOOLTIP_CLEARANCE.toPx() }
        val selectedBoundsInParent = selectedBounds?.let { bounds ->
            Rect(
                left = bounds.left + heatmapOriginInParent.x,
                top = bounds.top + heatmapOriginInParent.y,
                right = bounds.right + heatmapOriginInParent.x,
                bottom = bounds.bottom + heatmapOriginInParent.y,
            )
        }
        val tooltipPlacement = if (selectedBoundsInParent != null) {
            placeHeatmapTooltip(
                viewportWidthPx = viewportWidthPx,
                cellBounds = selectedBoundsInParent,
                tooltipWidthPx = tooltipWidthPx,
                tooltipHeightPx = tooltipHeightPx,
                selectedScale = HEATMAP_SELECTED_SCALE,
                clearancePx = tooltipClearancePx,
            )
        } else {
            null
        }
        val tooltipPresentation = if (selectedDay != null && tooltipPlacement != null) {
            HeatmapTooltipPresentation(
                day = selectedDay,
                target = Offset(tooltipPlacement.x, tooltipPlacement.y),
            )
        } else {
            null
        }
        LaunchedEffect(tooltipPresentation) {
            onTooltipChanged(tooltipPresentation)
        }
        val containerHeight = gridHeight

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
                    var gestureState: HeatmapGestureState? = null
                    detectTokenHeatmapGestures(
                        onSelectionStart = { point ->
                            val index = geometry.hitTest(
                                point = point,
                            )
                            val date = index?.let(geometry::indexToDate)
                            val state = heatmapGestureOnDown(latestSelectedDate.value, date)
                            if (state == null) {
                                gestureState = null
                                false
                            } else {
                                gestureState = state
                                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                                if (!state.startedOnSelected) {
                                    onSelected(state.currentScrubDate)
                                }
                                true
                            }
                        },
                        onSelectionMove = { point ->
                            val index = geometry.hitTest(
                                point = point,
                            )
                            val date = index?.let(geometry::indexToDate)
                            gestureState?.let { currentState ->
                                val nextState = heatmapGestureOnMove(currentState, date)
                                if (nextState.currentScrubDate != currentState.currentScrubDate) {
                                    onSelected(nextState.currentScrubDate)
                                }
                                gestureState = nextState
                            }
                        },
                        onSelectionEnd = {
                            gestureState?.let { state ->
                                if (heatmapGestureShouldClear(state)) {
                                    onClearSelection()
                                }
                            }
                            gestureState = null
                        },
                    )
                },
        ) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(gridHeight),
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

            renderedVisualSelection?.let { selection ->
                HeatmapSelectedCell(
                    color = selection.color,
                    cellSize = gridCellSize,
                    scale = selectedScaleAnimation.value,
                    modifier = Modifier
                        .offset {
                            IntOffset(
                                selection.bounds.left.roundToInt(),
                                selection.bounds.top.roundToInt(),
                            )
                        }
                        .zIndex(1f),
                )
            }
        }
    }
}

@Composable
private fun HeatmapSelectedCell(
    color: androidx.compose.ui.graphics.Color,
    cellSize: androidx.compose.ui.unit.Dp,
    scale: Float,
    modifier: Modifier = Modifier,
) {
    val shape = RoundedCornerShape(HEATMAP_CORNER_RADIUS)
    val edgeColor = lerp(color, Color.White, 0.24f)
    Box(
        modifier
            .size(cellSize)
            .graphicsLayer {
                scaleX = scale
                scaleY = scale
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
private fun HeatmapBlurTooltip(
    day: TokenUsageDay,
    hazeState: HazeState,
    modifier: Modifier = Modifier,
) {
    val tooltipTint = Color(0xff121212).copy(alpha = 0.72f)
    val shape = RoundedCornerShape(16.dp)
    val scale = remember { Animatable(0.96f) }
    LaunchedEffect(Unit) {
        scale.animateTo(1f, tween(160))
    }
    Box(
        modifier
            .width(HEATMAP_TOOLTIP_WIDTH)
            .height(HEATMAP_TOOLTIP_HEIGHT)
            .graphicsLayer {
                scaleX = scale.value
                scaleY = scale.value
                transformOrigin = TransformOrigin.Center
            }
            .clip(shape)
            .hazeEffect(hazeState) {
                blurEffect {
                    blurRadius = 24.dp
                    colorEffects = listOf(HazeColorEffect.tint(tooltipTint))
                }
            }
            .border(1.dp, Color.White.copy(alpha = 0.18f), shape)
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
                color = Color.White,
                fontSize = 18.sp,
                fontWeight = FontWeight.SemiBold,
                maxLines = 1,
            )
            Text(
                formatHeatmapTooltipDate(day.date),
                color = Color.White.copy(alpha = 0.72f),
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
private val HEATMAP_TOOLTIP_CLEARANCE = 32.dp
internal const val HEATMAP_SELECTED_SCALE = 1.5f

private fun formatSyncTime(raw: String) = runCatching { SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw))) }.getOrDefault("未知")
