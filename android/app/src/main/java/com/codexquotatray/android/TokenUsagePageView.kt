package com.codexquotatray.android

import android.content.Intent
import android.content.BroadcastReceiver
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
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
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.shadow.Shadow
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
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.codexquotatray.android.usage.HeatmapBuckets
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.usage.DataTransport
import com.codexquotatray.android.usage.TokenUsageScope
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
import com.codexquotatray.android.usage.isLanAttemptStale
import com.codexquotatray.android.source.AndroidDataSourcePriorityStore
import com.codexquotatray.android.source.DataSourcePriority
import com.codexquotatray.android.source.sourcePriorityChanged
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

internal class TokenUsagePageController(private val host: MainActivity) {
    private val cache by lazy { TokenUsageCache(host) }
    private val store by lazy { TokenSyncStore(host) }
    private val refreshSettings by lazy { TokenUsageRefreshSettingsStore(host) }
    private val oauthStore by lazy { OAuthStore(host) }
    private val sourcePriorityStore by lazy { AndroidDataSourcePriorityStore(host) }
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
    private var lastObservedPriority: DataSourcePriority? = null

    val canSync get() = !syncing && (store.load() != null || oauthStore.hasCredentials())

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
        val priorityChanged = reconcilePairingState()
        if (!priorityChanged) requestSync(reason)
    }

    fun onNetworkRestored() {
        if (!visible || syncing || !canSync) return
        requestSync(AutomaticRefreshReason.NETWORK_RESTORED)
    }

    /**
     * Reconciles local provider/cache state. A changed source priority also
     * starts one source-change sync while retaining the current snapshot.
     */
    fun reconcilePairingState(): Boolean {
        val priorityChanged = observeTokenPriority()
        val currentPairing = store.load()
        val hasOAuth = oauthStore.hasCredentials()
        val cached = cache.loadForAvailableSources(currentPairing, hasOAuth)
        if (currentPairing == null && !hasOAuth) {
            paired = false
            snapshot = null
            status = RefreshStatusFormatter.tokenUnpaired()
            return false
        }
        paired = true
        if (cached != null && (snapshot == null || hasNewerTokenUsageSnapshot(snapshot, cached))) {
            snapshot = cached
            status = RefreshStatusFormatter.loaded(tokenUsageSourceLabel(cached), formatSyncTime(cached.generatedAtUtc))
        } else if (snapshot == null) {
            status = RefreshStatusFormatter.tokenPairedWithoutData()
        }
        if (priorityChanged) requestSync(AutomaticRefreshReason.SOURCE_CHANGED)
        return priorityChanged
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
    fun requestSync() { if (canSync) requestSync(AutomaticRefreshReason.MANUAL) }

    private fun requestSync(reason: AutomaticRefreshReason) {
        if (!canSync || destroyed) return
        val enabled = refreshSettings.load().autoSyncOnOpen
        if (!AppAutomaticRefreshCoordinator.tryStart(AutomaticRefreshChannel.TOKEN, reason, enabled)) return
        val presentationStartedAt = SystemClock.elapsedRealtime()
        val snapshotAtStart = snapshot
        val statusAtStart = status
        syncing = true
        status = RefreshStatusFormatter.tokenRefreshing(snapshot != null)
        worker.execute {
            val result = try {
                runCatching {
                    TokenUsageSyncCoordinator(host).sync(
                        forceRefresh = shouldForceTokenUsageRefresh(reason),
                    )
                }
            } finally {
                AppAutomaticRefreshCoordinator.finish(AutomaticRefreshChannel.TOKEN)
            }
            val requestFinishedAt = SystemClock.elapsedRealtime()
            main.post {
                val remaining = remainingRefreshPresentationMillis(
                    presentationStartedAt,
                    requestFinishedAt,
                )
                main.postDelayed({
                    if (destroyed) return@postDelayed
                    syncing = false
                    result.onSuccess { synced ->
                        snapshot = synced.snapshot
                        status = RefreshStatusFormatter.loaded(tokenUsageSourceLabel(synced.snapshot), formatSyncTime(synced.snapshot.generatedAtUtc))
                    }.onFailure { error ->
                        if (error.isLanAttemptStale()) {
                            status = statusAtStart
                        } else {
                            val latestSnapshot = loadCachedSnapshot()
                            if (latestSnapshot != null && hasNewerTokenUsageSnapshot(snapshotAtStart, latestSnapshot)) {
                                snapshot = latestSnapshot
                                status = RefreshStatusFormatter.loaded(tokenUsageSourceLabel(latestSnapshot), formatSyncTime(latestSnapshot.generatedAtUtc))
                            } else {
                                val message = tokenUsageSyncErrorMessage(error)
                                status = RefreshStatusFormatter.tokenFailure(
                                    reason = message,
                                    updatedAt = snapshot?.let { formatSyncTime(it.generatedAtUtc) },
                                )
                            }
                        }
                    }
                }, remaining)
            }
        }
    }

    private fun loadCachedSnapshot(): TokenUsageSnapshot? =
        cache.loadForAvailableSources(store.load(), oauthStore.hasCredentials())

    private fun observeTokenPriority(): Boolean {
        val currentPriority = sourcePriorityStore.load().token
        val changed = sourcePriorityChanged(lastObservedPriority, currentPriority)
        lastObservedPriority = currentPriority
        return changed
    }
}

internal fun tokenUsageSourceLabel(snapshot: TokenUsageSnapshot): String = when {
    snapshot.transport == DataTransport.OPENAI -> "OpenAI · 账户"
    snapshot.scope == TokenUsageScope.ACCOUNT -> "Windows · 账户"
    else -> "Windows · 本机"
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
internal fun TokenUsagePage(
    controller: TokenUsagePageController,
    onPairing: () -> Unit,
    onLoginOpenAi: () -> Unit,
    modifier: Modifier = Modifier,
) {
    TokenUsagePageContent(
        status = controller.status,
        paired = controller.paired,
        snapshot = controller.snapshot,
        onPairing = onPairing,
        onLoginOpenAi = onLoginOpenAi,
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()),
    )
}

@Composable
internal fun TokenUsagePageContent(
    status: String,
    paired: Boolean,
    snapshot: TokenUsageSnapshot?,
    onPairing: () -> Unit,
    onLoginOpenAi: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    Column(modifier.padding(horizontal = 20.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("统计", fontSize = 21.sp, fontWeight = FontWeight.Bold, color = palette.color(palette.title))
        TokenUsageStatusLine(status)
        if (!paired) {
            DataSourceEmptyStateCard(
                message = "登录 OpenAI 或连接 Windows CodexQuotaTray 后，即可查看 Token 使用历史。",
                onLoginOpenAi = onLoginOpenAi,
                onPairWindows = onPairing,
            )
        } else snapshot?.let { TokenUsageContent(it) }
        Spacer(Modifier.height(96.dp))
    }
}

@Composable
private fun TokenUsageStatusLine(status: String) {
    RefreshStatusLine(status)
}

private data class TokenUsagePresentation(
    val first: List<Pair<String, Long?>>,
    val second: List<Pair<String, Long?>>,
    val categories: List<Pair<String, Long?>>,
)

@Composable
private fun TokenUsageContent(snapshot: TokenUsageSnapshot) {
    val palette = LocalQuotaPalette.current
    val presentation = remember(snapshot) {
        TokenUsagePresentation(
            first = listOf(
                "今日 Token" to snapshot.summary.todayTokens,
                "7 天 Token" to snapshot.summary.last7DaysTokens,
                "30 天 Token" to snapshot.summary.last30DaysTokens,
                "累计 Token" to snapshot.summary.lifetimeTokens,
            ),
            second = listOf(
                "峰值 Token" to snapshot.summary.peakDailyTokens,
                "当前连续" to snapshot.summary.currentStreak?.toLong(),
                "最长连续" to snapshot.summary.longestStreak?.toLong(),
            ),
            categories = listOf(
                "输入" to completeCategoryTotal(snapshot.days) { it.inputTokens },
                "缓存输入" to completeCategoryTotal(snapshot.days) { it.cachedInputTokens },
                "输出" to completeCategoryTotal(snapshot.days) { it.outputTokens },
                "推理" to completeCategoryTotal(snapshot.days) { it.reasoningTokens },
            ),
        )
    }
    val tokenContentBackdrop = rememberLayerBackdrop()
    var selectedDate by remember { mutableStateOf<LocalDate?>(null) }
    var tooltipPresentation by remember { mutableStateOf<HeatmapTooltipPresentation?>(null) }
    val tooltipNeedsBackdrop = selectedDate != null || tooltipPresentation != null
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
                .then(if (tooltipNeedsBackdrop) Modifier.layerBackdrop(tokenContentBackdrop) else Modifier)
                .background(palette.color(palette.background)),
        ) {
            SummaryRow(presentation.first)
            SummaryRow(presentation.second)
            if (shouldShowTokenCategories(presentation.categories)) {
                SummaryRow(presentation.categories)
            }
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
            HeatmapLiquidTooltip(
                day = presentation.day,
                backdrop = tokenContentBackdrop,
                modifier = Modifier
                    .offset {
                        IntOffset(tooltipOffset.x.roundToInt(), tooltipOffset.y.roundToInt())
                    }
                    .zIndex(2f),
            )
        }
    }
}

internal fun shouldShowTokenCategories(categories: List<Pair<String, Long?>>): Boolean =
    categories.any { (_, value) -> value != null }

private fun completeCategoryTotal(
    days: List<TokenUsageDay>,
    select: (TokenUsageDay) -> Long?,
): Long? {
    if (days.isEmpty() || days.any { select(it) == null }) return null
    var total = 0L
    days.forEach { day ->
        val value = select(day) ?: return null
        total = if (Long.MAX_VALUE - total < value) Long.MAX_VALUE else total + value
    }
    return total
}

@Composable
private fun SummaryRow(items: List<Pair<String, Long?>>) {
    val palette = LocalQuotaPalette.current
    Row(Modifier.fillMaxWidth()) {
        items.forEach { (label, value) ->
            Column(Modifier.weight(1f).padding(vertical = 10.dp, horizontal = 3.dp)) {
                Text(
                    tokenSummaryValueLabel(label, value),
                    Modifier.fillMaxWidth(),
                    color = palette.color(palette.title),
                    fontSize = if (value == null && label == "今日 Token") 13.sp else 18.sp,
                    fontWeight = FontWeight.Bold,
                    textAlign = TextAlign.Center,
                )
                Text(label, Modifier.fillMaxWidth(), color = palette.color(palette.muted), fontSize = 11.sp, textAlign = TextAlign.Center)
            }
        }
    }
}

internal fun tokenSummaryValueLabel(label: String, value: Long?): String = when {
    value != null -> TokenFormatter.format(value)
    label == "今日 Token" -> "待同步"
    else -> "—"
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
    val bucketScale = remember(days) {
        HeatmapBuckets.prepare(days.map { it.totalTokens })
    }
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
        val bucketIndices = remember(days, range.start, range.dayCount) {
            IntArray(range.dayCount) { index ->
                val date = range.start.plusDays(index.toLong())
                bucketScale.bucket(values[date]?.totalTokens ?: 0L)
            }
        }
        val selectedIndex = selectedDate?.let { ChronoUnit.DAYS.between(range.start, it).toInt() }
        val selectedBounds = selectedIndex?.let(geometry::cellBounds)
        val selectedDay = if (selectedDate != null && selectedBounds != null) {
            values[selectedDate] ?: TokenUsageDay(selectedDate, 0, null, null, null, null)
        } else {
            null
        }
        val selectedBucket = selectedIndex?.let(bucketIndices::getOrNull)
        val currentVisualSelection = if (
            selectedDate != null &&
            selectedBounds != null &&
            selectedDay != null &&
            selectedBucket != null
        ) {
            HeatmapVisualSelection(
                date = selectedDate,
                bounds = selectedBounds,
                color = colors[selectedBucket],
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
                            val column = index / geometry.rowCount
                            val row = index % geometry.rowCount
                            drawRoundRect(
                                color = colors[bucketIndices[index]],
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
private fun HeatmapLiquidTooltip(
    day: TokenUsageDay,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    val isDark = palette.color(palette.background).luminance() < 0.35f
    val scale = remember { Animatable(0.96f) }
    LaunchedEffect(Unit) {
        scale.animateTo(1f, tween(160))
    }
    val tooltipModifier = liquidTokenDialogSurfaceModifier(
        modifier = modifier
            .width(HEATMAP_TOOLTIP_WIDTH)
            .height(HEATMAP_TOOLTIP_HEIGHT)
            .graphicsLayer {
                scaleX = scale.value
                scaleY = scale.value
                transformOrigin = TransformOrigin.Center
            },
        backdrop = backdrop,
        isDark = isDark,
    )
    Box(
        tooltipModifier.semantics {
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
                color = palette.color(palette.secondary),
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
