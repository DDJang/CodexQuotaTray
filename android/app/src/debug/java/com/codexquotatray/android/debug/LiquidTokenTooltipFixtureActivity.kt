package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.VisibilityThreshold
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.dropShadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.shadow.Shadow
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.DpOffset
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.HeatmapGeometry
import com.codexquotatray.android.HEATMAP_SELECTED_SCALE
import com.codexquotatray.android.TOKEN_HEATMAP_COLUMNS
import com.codexquotatray.android.TOKEN_HEATMAP_ROWS
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.LocalQuotaPalette
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.ThemePalette
import com.codexquotatray.android.color
import com.codexquotatray.android.detectTokenHeatmapGestures
import com.codexquotatray.android.formatHeatmapTooltipDate
import com.codexquotatray.android.formatHeatmapTooltipTokenCount
import com.codexquotatray.android.heatmapGestureOnDown
import com.codexquotatray.android.heatmapGestureOnMove
import com.codexquotatray.android.heatmapGestureShouldClear
import com.codexquotatray.android.rememberSystemHapticClick
import com.codexquotatray.android.placeHeatmapTooltip
import com.codexquotatray.android.usage.HeatmapBuckets
import com.codexquotatray.android.usage.TokenUsageDay
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.colorControls
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.shadow.InnerShadow
import com.kyant.shapes.RoundedRectangle
import dev.chrisbanes.haze.HazeState
import dev.chrisbanes.haze.blur.HazeColorEffect
import dev.chrisbanes.haze.blur.blurEffect
import dev.chrisbanes.haze.hazeEffect
import dev.chrisbanes.haze.hazeSource
import dev.chrisbanes.haze.rememberHazeState
import java.time.LocalDate
import kotlin.math.roundToInt

private enum class FixtureTooltipStyle(
    val selectorLabel: String,
    val displayLabel: String,
) {
    CURRENT("Current", "Current Haze"),
    DIALOG("Dialog", "Kyant Dialog"),
    MAGNIFIER("Magnifier", "Kyant Magnifier"),
}

private data class FixtureTooltipPresentation(
    val day: TokenUsageDay,
    val target: Offset,
)

private data class FixtureVisualSelection(
    val date: LocalDate,
    val bounds: Rect,
    val color: Color,
)

private val fixtureHeatmapStartDate = LocalDate.of(2026, 5, 10)
private val fixtureTooltipWidth = 220.dp
private val fixtureTooltipHeight = 64.dp
private val fixtureTooltipClearance = 32.dp
private val fixtureHeatmapGap = 5.dp
private val fixtureHeatmapMaxCellSize = 24.dp
private val fixtureHeatmapCornerRadius = 3.dp
private val fixtureTooltipShape = RoundedCornerShape(16.dp)
private val fixtureHeatmapColors = listOf(
    Color(0xFF3D4A58),
    Color(0xFF718894),
    Color(0xFF7BC96F),
    Color(0xFF239A3B),
    Color(0xFF72E56E),
)

class LiquidTokenTooltipFixtureActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        val selectedTheme = AppTheme.mode(this)
        setTheme(
            if (AppTheme.effectiveMode(this, selectedTheme) == ThemeMode.DARK) {
                android.R.style.Theme_Material_NoActionBar
            } else {
                android.R.style.Theme_Material_Light_NoActionBar
            },
        )
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        val palette = AppTheme.palette(this, selectedTheme)
        setContent {
            CodexQuotaTheme(palette) {
                LiquidTokenTooltipFixtureScreen()
            }
        }
    }
}

@Composable
private fun LiquidTokenTooltipFixtureScreen() {
    val palette = LocalQuotaPalette.current
    val backdrop = rememberLayerBackdrop()
    val hazeState = rememberHazeState()
    val days = remember { fixtureTokenDays() }
    var style by remember { mutableStateOf(FixtureTooltipStyle.CURRENT) }
    var selectedDate by remember { mutableStateOf(fixtureHeatmapStartDate.plusDays(89)) }
    var tooltipPresentation by remember { mutableStateOf<FixtureTooltipPresentation?>(null) }
    val tooltipOffset = rememberFixtureTooltipOffset(tooltipPresentation?.target)
    val pageBackground = palette.color(palette.background)

    Box(Modifier.fillMaxSize()) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
                .background(pageBackground)
                .hazeSource(hazeState),
        ) {
            Column(
                Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .navigationBarsPadding()
                    .padding(horizontal = 18.dp, vertical = 18.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(
                    "Token Heatmap Tooltip Fixture",
                    color = palette.color(palette.title),
                    style = MaterialTheme.typography.headlineSmall,
                )
                Text(
                    "Local fake data · 13 columns × 7 rows · tap / scrub to move one tooltip",
                    color = palette.color(palette.secondary),
                    style = MaterialTheme.typography.bodyMedium,
                )
                FixtureTooltipStyleSelector(
                    selectedStyle = style,
                    onStyleSelected = { style = it },
                )
                Text(
                    "Tooltip style: ${style.displayLabel}",
                    color = palette.color(palette.body),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Medium,
                )
                FixtureSummaryBlocks(palette)
                Text(
                    "Token heatmap",
                    color = palette.color(palette.title),
                    fontSize = 18.sp,
                    fontWeight = FontWeight.SemiBold,
                )
                FixtureTokenHeatmap(
                    days = days,
                    selectedDate = selectedDate,
                    onSelected = { selectedDate = it },
                    onClearSelection = { selectedDate = null },
                    onTooltipChanged = { tooltipPresentation = it },
                )
                Spacer(Modifier.height(12.dp))
                Text(
                    "A/B/C 只替换 Tooltip surface drawing；日期、placement、spring、selection 和 haptic 共用同一套逻辑。",
                    color = palette.color(palette.secondary),
                    fontSize = 12.sp,
                )
            }
        }

        tooltipPresentation?.let { presentation ->
            FixtureTokenTooltip(
                day = presentation.day,
                style = style,
                backdrop = backdrop,
                hazeState = hazeState,
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
private fun FixtureTooltipStyleSelector(
    selectedStyle: FixtureTooltipStyle,
    onStyleSelected: (FixtureTooltipStyle) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(palette.color(palette.surface))
            .padding(4.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        FixtureTooltipStyle.entries.forEach { candidate ->
            val selected = candidate == selectedStyle
            Text(
                text = candidate.selectorLabel,
                modifier = Modifier
                    .weight(1f)
                    .clip(RoundedCornerShape(10.dp))
                    .background(
                        if (selected) palette.color(palette.accent)
                        else Color.Transparent,
                    )
                    .clickable(
                        onClick = rememberSystemHapticClick { onStyleSelected(candidate) },
                    )
                    .padding(vertical = 9.dp),
                color = if (selected) Color.White else palette.color(palette.body),
                fontSize = 14.sp,
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                maxLines = 1,
            )
        }
    }
}

@Composable
private fun FixtureSummaryBlocks(palette: ThemePalette) {
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        FixtureSummaryCard(
            title = "今日 Token",
            value = "37.9M",
            modifier = Modifier.weight(1f),
            palette = palette,
        )
        FixtureSummaryCard(
            title = "7 天 Token",
            value = "84.2M",
            modifier = Modifier.weight(1f),
            palette = palette,
        )
        FixtureSummaryCard(
            title = "活跃天数",
            value = "42",
            modifier = Modifier.weight(1f),
            palette = palette,
        )
    }
}

@Composable
private fun FixtureSummaryCard(
    title: String,
    value: String,
    modifier: Modifier,
    palette: ThemePalette,
) {
    Card(
        modifier,
        shape = RoundedCornerShape(14.dp),
        colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface)),
    ) {
        Column(
            Modifier.padding(horizontal = 12.dp, vertical = 10.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text(title, color = palette.color(palette.secondary), fontSize = 12.sp, maxLines = 1)
            Text(value, color = palette.color(palette.title), fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
        }
    }
}

@Composable
private fun FixtureTokenHeatmap(
    days: List<TokenUsageDay>,
    selectedDate: LocalDate?,
    onSelected: (LocalDate) -> Unit,
    onClearSelection: () -> Unit,
    onTooltipChanged: (FixtureTooltipPresentation?) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val density = LocalDensity.current
    val hapticFeedback = LocalHapticFeedback.current
    val latestSelectedDate = rememberUpdatedState(selectedDate)
    val values = remember(days) { days.associateBy { it.date } }
    val nonZero = remember(days) { days.map { it.totalTokens }.filter { it > 0L } }
    // Tooltip offset is applied by the outer Root Box, so convert the heatmap
    // origin to the same Compose-root coordinate space before placing it.
    var heatmapOriginInRoot by remember { mutableStateOf(Offset.Zero) }

    BoxWithConstraints(
        Modifier
            .fillMaxWidth()
            .onGloballyPositioned { coordinates ->
                heatmapOriginInRoot = coordinates.positionInRoot()
            },
    ) {
        val viewportWidthPx = with(density) { maxWidth.toPx() }
        val geometry = remember(maxWidth, density) {
            val gapPx = with(density) { fixtureHeatmapGap.toPx() }
            val maxCellSizePx = with(density) { fixtureHeatmapMaxCellSize.toPx() }
            val cellSizePx = ((viewportWidthPx - (TOKEN_HEATMAP_COLUMNS - 1) * gapPx) / TOKEN_HEATMAP_COLUMNS)
                .coerceAtLeast(1f)
                .coerceAtMost(maxCellSizePx)
            val contentWidthPx = TOKEN_HEATMAP_COLUMNS * cellSizePx + (TOKEN_HEATMAP_COLUMNS - 1) * gapPx
            HeatmapGeometry(
                cellSizePx = cellSizePx,
                gapPx = gapPx,
                startDate = fixtureHeatmapStartDate,
                dayCount = days.size,
                contentOffsetX = com.codexquotatray.android.centeredHeatmapOffset(viewportWidthPx, contentWidthPx),
            )
        }
        val gridWidth = with(density) { geometry.contentWidthPx.toDp() }
        val gridHeight = with(density) { geometry.contentHeightPx.toDp() }
        val gridCellSize = with(density) { geometry.cellSizePx.toDp() }
        val selectedIndex = selectedDate?.let { date ->
            java.time.temporal.ChronoUnit.DAYS.between(fixtureHeatmapStartDate, date).toInt()
        }
        val selectedBounds = selectedIndex?.let(geometry::cellBounds)
        val selectedDay = selectedDate?.let(values::get)
        val currentVisualSelection = if (selectedDate != null && selectedBounds != null && selectedDay != null) {
            FixtureVisualSelection(
                date = selectedDate,
                bounds = selectedBounds,
                color = fixtureHeatmapColors[HeatmapBuckets.bucket(selectedDay.totalTokens, nonZero)],
            )
        } else {
            null
        }
        var visualSelection by remember { mutableStateOf<FixtureVisualSelection?>(null) }
        val selectedScaleAnimation = remember { Animatable(1f) }
        LaunchedEffect(currentVisualSelection) {
            if (currentVisualSelection != null) {
                val wasVisible = visualSelection != null
                visualSelection = currentVisualSelection
                if (!wasVisible) selectedScaleAnimation.snapTo(1f)
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
        val tooltipWidthPx = with(density) { fixtureTooltipWidth.toPx() }
        val tooltipHeightPx = with(density) { fixtureTooltipHeight.toPx() }
        val tooltipClearancePx = with(density) { fixtureTooltipClearance.toPx() }
        val selectedBoundsInRoot = selectedBounds?.let { bounds ->
            Rect(
                left = bounds.left + heatmapOriginInRoot.x,
                top = bounds.top + heatmapOriginInRoot.y,
                right = bounds.right + heatmapOriginInRoot.x,
                bottom = bounds.bottom + heatmapOriginInRoot.y,
            )
        }
        val tooltipPlacement = selectedBoundsInRoot?.let { bounds ->
            placeHeatmapTooltip(
                viewportWidthPx = viewportWidthPx,
                cellBounds = bounds,
                tooltipWidthPx = tooltipWidthPx,
                tooltipHeightPx = tooltipHeightPx,
                selectedScale = HEATMAP_SELECTED_SCALE,
                clearancePx = tooltipClearancePx,
            )
        }
        val tooltipPresentation = if (selectedDay != null && tooltipPlacement != null) {
            FixtureTooltipPresentation(
                day = selectedDay,
                target = Offset(tooltipPlacement.x, tooltipPlacement.y),
            )
        } else {
            null
        }
        LaunchedEffect(tooltipPresentation) {
            onTooltipChanged(tooltipPresentation)
        }

        Box(
            Modifier
                .fillMaxWidth()
                .height(gridHeight)
                .semantics {
                    contentDescription = selectedDay?.let {
                        "${formatHeatmapTooltipDate(it.date)}，${formatHeatmapTooltipTokenCount(it.totalTokens)}"
                    } ?: "Token 使用热力图"
                }
                .pointerInput(geometry) {
                    var gestureState: com.codexquotatray.android.HeatmapGestureState? = null
                    detectTokenHeatmapGestures(
                        onSelectionStart = { point ->
                            val index = geometry.hitTest(point)
                            val date = index?.let(geometry::indexToDate)
                            val state = heatmapGestureOnDown(latestSelectedDate.value, date)
                            if (state == null) {
                                gestureState = null
                                false
                            } else {
                                gestureState = state
                                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                                if (!state.startedOnSelected) onSelected(state.currentScrubDate)
                                true
                            }
                        },
                        onSelectionMove = { point ->
                            val index = geometry.hitTest(point)
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
                                if (heatmapGestureShouldClear(state)) onClearSelection()
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
                        .align(Alignment.Center),
                ) {
                    Canvas(Modifier.fillMaxSize()) {
                        repeat(days.size) { index ->
                            val date = fixtureHeatmapStartDate.plusDays(index.toLong())
                            val tokens = values[date]?.totalTokens ?: 0L
                            val column = index / TOKEN_HEATMAP_ROWS
                            val row = index % TOKEN_HEATMAP_ROWS
                            drawRoundRect(
                                color = fixtureHeatmapColors[HeatmapBuckets.bucket(tokens, nonZero)],
                                topLeft = Offset(column * geometry.stridePx, row * geometry.stridePx),
                                size = Size(geometry.cellSizePx, geometry.cellSizePx),
                                cornerRadius = CornerRadius(
                                    with(density) { fixtureHeatmapCornerRadius.toPx() },
                                ),
                            )
                        }
                    }
                }
            }
            renderedVisualSelection?.let { selection ->
                FixtureSelectedCell(
                    color = selection.color,
                    cellSize = gridCellSize,
                    scale = selectedScaleAnimation.value,
                    modifier = Modifier
                        .offset {
                            IntOffset(selection.bounds.left.roundToInt(), selection.bounds.top.roundToInt())
                        }
                        .zIndex(1f),
                )
            }
        }
    }
}

@Composable
private fun FixtureSelectedCell(
    color: Color,
    cellSize: androidx.compose.ui.unit.Dp,
    scale: Float,
    modifier: Modifier = Modifier,
) {
    val shape = RoundedCornerShape(fixtureHeatmapCornerRadius)
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
private fun FixtureTokenTooltip(
    day: TokenUsageDay,
    style: FixtureTooltipStyle,
    backdrop: com.kyant.backdrop.Backdrop,
    hazeState: HazeState,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    val isDark = palette.color(palette.background).luminance() < 0.35f
    val currentContainerColor = if (isDark) Color(0xFF121212) else palette.color(palette.surface)
    val currentBorderColor = if (isDark) {
        Color.White.copy(alpha = 0.18f)
    } else {
        palette.color(palette.border).copy(alpha = 0.9f)
    }
    val appearanceScale = remember { Animatable(0.96f) }
    LaunchedEffect(Unit) {
        appearanceScale.animateTo(1f, tween(160))
    }
    val baseModifier = modifier
        .width(fixtureTooltipWidth)
        .height(fixtureTooltipHeight)
        .graphicsLayer {
            scaleX = appearanceScale.value
            scaleY = appearanceScale.value
            transformOrigin = TransformOrigin.Center
        }
    val styledModifier = when (style) {
        FixtureTooltipStyle.CURRENT -> baseModifier
            .clip(fixtureTooltipShape)
            .hazeEffect(hazeState) {
                blurEffect {
                    blurRadius = 24.dp
                    backgroundColor = currentContainerColor
                    colorEffects = listOf(
                        HazeColorEffect.tint(
                            currentContainerColor.copy(alpha = if (isDark) 0.65f else 0.72f),
                        ),
                    )
                }
            }
            .border(1.dp, currentBorderColor, fixtureTooltipShape)
        FixtureTooltipStyle.DIALOG -> baseModifier.drawBackdrop(
            backdrop = backdrop,
            shape = { RoundedRectangle(16.dp) },
            effects = {
                colorControls(brightness = 0f, saturation = 1.5f, contrast = 1f)
                blur(8.dp.toPx())
                lens(24.dp.toPx(), 48.dp.toPx(), depthEffect = true)
            },
            highlight = { Highlight.Plain },
            onDrawSurface = { drawRect(Color(0xFF121212).copy(alpha = 0.4f)) },
        )
        FixtureTooltipStyle.MAGNIFIER -> baseModifier.drawBackdrop(
            backdrop = backdrop,
            shape = { RoundedRectangle(16.dp) },
            effects = {
                lens(
                    8.dp.toPx(),
                    24.dp.toPx(),
                    depthEffect = true,
                    chromaticAberration = true,
                )
            },
            innerShadow = { InnerShadow(radius = 16.dp) },
        )
    }
    Box(
        styledModifier.semantics {
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

@Composable
private fun rememberFixtureTooltipOffset(target: Offset?): Offset {
    val positionAnimation = remember {
        Animatable(
            initialValue = Offset.Zero,
            typeConverter = Offset.VectorConverter,
            visibilityThreshold = Offset.VisibilityThreshold,
        )
    }
    var positionInitialized by remember { mutableStateOf(false) }
    LaunchedEffect(target) {
        if (target == null) {
            positionInitialized = false
        } else if (!positionInitialized) {
            positionAnimation.snapTo(target)
            positionInitialized = true
        } else {
            positionAnimation.animateTo(
                targetValue = target,
                animationSpec = spring(
                    dampingRatio = 0.5f,
                    stiffness = 300f,
                    visibilityThreshold = Offset.VisibilityThreshold,
                ),
            )
        }
    }
    return if (target != null && !positionInitialized) target else positionAnimation.value
}

private fun fixtureTokenDays(): List<TokenUsageDay> = buildList {
    repeat(TOKEN_HEATMAP_COLUMNS * TOKEN_HEATMAP_ROWS) { index ->
        add(
            TokenUsageDay(
                date = fixtureHeatmapStartDate.plusDays(index.toLong()),
                totalTokens = fixtureTokenCount(index),
                inputTokens = null,
                cachedInputTokens = null,
                outputTokens = null,
                reasoningTokens = null,
            ),
        )
    }
}

private fun fixtureTokenCount(index: Int): Long {
    if ((index / TOKEN_HEATMAP_ROWS + index % TOKEN_HEATMAP_ROWS) % 7 == 0) return 0L
    if (index == 89) return 37_984_660L
    return when ((index * 3 + index / TOKEN_HEATMAP_ROWS) % 5) {
        0 -> 4_000L
        1 -> 40_000L
        2 -> 500_000L
        3 -> 4_000_000L
        else -> 16_000_000L
    }
}
