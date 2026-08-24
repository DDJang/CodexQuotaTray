package com.codexquotatray.android

import android.content.pm.ApplicationInfo
import android.util.Log

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseOut
import androidx.compose.animation.core.spring
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.BasicText
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.draw.paint
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.GraphicsLayerScope
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.layout.LayoutCoordinates
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalViewConfiguration
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.TextStyle
import androidx.annotation.DrawableRes
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.fastCoerceAtMost
import androidx.compose.ui.util.fastFirstOrNull
import androidx.compose.ui.util.fastRoundToInt
import androidx.compose.ui.util.lerp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberCombinedBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.colorControls
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.shadow.InnerShadow
import com.kyant.backdrop.shadow.Shadow
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sign
import kotlin.math.sin
import kotlin.math.tanh

@Composable
internal fun GlassSurface(
    backdrop: Backdrop,
    shape: Shape,
    modifier: Modifier = Modifier,
    clippedModifier: Modifier = Modifier,
    layerBlock: GraphicsLayerScope.() -> Unit = {},
    contentAlignment: Alignment = Alignment.Center,
    blurRadius: Dp = 2.dp,
    refractionHeight: Dp = 12.dp,
    refractionAmount: Dp = 24.dp,
    enableVibrancy: Boolean = true,
    lensDepthEffect: Boolean = false,
    enableColorControls: Boolean = false,
    saturation: Float = 1f,
    highlight: Highlight? = null,
    surfaceAlpha: Float = 0.2f,
    surfaceColor: Color? = null,
    content: @Composable BoxScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val resolvedSurfaceColor = surfaceColor ?: palette.color(palette.surface)
    Box(
        modifier
            .drawBackdrop(
                backdrop = backdrop,
                shape = { shape },
                effects = {
                    if (enableVibrancy) {
                        vibrancy()
                    }
                    if (enableColorControls) {
                        colorControls(brightness = 0f, saturation = saturation, contrast = 1f)
                    }
                    blur(blurRadius.toPx())
                    lens(refractionHeight.toPx(), refractionAmount.toPx(), depthEffect = lensDepthEffect)
                },
                highlight = { highlight ?: Highlight.Default },
                layerBlock = layerBlock,
                onDrawSurface = { drawRect(resolvedSurfaceColor.copy(alpha = surfaceAlpha)) },
            )
            .clip(shape)
            .then(clippedModifier),
        contentAlignment = contentAlignment,
        content = content,
    )
}

@Composable
internal fun GlassIconButton(
    @DrawableRes iconRes: Int,
    description: String,
    backdrop: Backdrop,
    enabled: Boolean = true,
    busy: Boolean = false,
    buttonSize: Dp = 52.dp,
    iconSize: Dp = 24.dp,
    onClick: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val animationScope = rememberCoroutineScope()
    val hapticOnClick = rememberSystemHapticClick(onClick)
    val interactiveHighlight = remember(animationScope) {
        GlassInteractiveHighlight(animationScope)
    }
    GlassSurface(
        backdrop = backdrop,
        shape = KyantShapes.capsule(),
        modifier = Modifier.size(buttonSize),
        clippedModifier = Modifier
            .clickable(
                interactionSource = null,
                indication = null,
                enabled = enabled,
                role = Role.Button,
                onClick = hapticOnClick,
            )
            .then(
                if (enabled) {
                    Modifier
                        .then(interactiveHighlight.modifier)
                        .then(interactiveHighlight.gestureModifier)
                } else {
                    Modifier
                },
            )
            .semantics { contentDescription = description }
            .alpha(if (enabled) 1f else 0.45f),
        layerBlock = {
                    val width = size.width
                    val height = size.height
                    val progress = interactiveHighlight.pressProgress
                    val scale = lerp(1f, 1f + 4.dp.toPx() / height, progress)
                    val maxOffset = size.minDimension
                    val initialDerivative = 0.05f
                    val rawOffset = interactiveHighlight.offset
                    val offset = Offset(
                        rawOffset.x.fastCoerceIn(-maxOffset, maxOffset),
                        rawOffset.y.fastCoerceIn(-maxOffset, maxOffset),
                    )
                    translationX = maxOffset * tanh(initialDerivative * offset.x / maxOffset)
                    translationY = maxOffset * tanh(initialDerivative * offset.y / maxOffset)
                    val maxDragScale = 4.dp.toPx() / height
                    val offsetAngle = atan2(offset.y, offset.x)
                    scaleX = scale + maxDragScale *
                        abs(cos(offsetAngle) * offset.x / size.maxDimension) *
                        (width / height).fastCoerceAtMost(1f)
                    scaleY = scale + maxDragScale *
                        abs(sin(offsetAngle) * offset.y / size.maxDimension) *
                        (height / width).fastCoerceAtMost(1f)
                },
    ) {
        if (busy) {
            CircularProgressIndicator(
                modifier = Modifier.size(iconSize),
                color = palette.color(palette.title),
                strokeWidth = 2.dp,
            )
        } else {
            Icon(
                painter = painterResource(iconRes),
                contentDescription = null,
                modifier = Modifier.size(iconSize),
                tint = palette.color(palette.title),
            )
        }
    }
}

private val LocalDockTabScale = compositionLocalOf { { 1f } }

@Composable
private fun RowScope.DockTab(
    selected: Boolean,
    onSelect: () -> Unit,
    content: @Composable ColumnScope.() -> Unit,
) {
    val scale = LocalDockTabScale.current
    Column(
        Modifier
            .clip(KyantShapes.capsule())
            .semantics {
                role = Role.Tab
                this.selected = selected
                onClick {
                    onSelect()
                    true
                }
            }
            .fillMaxHeight()
            .weight(1f)
            .graphicsLayer {
                val selectedScale = if (selected) 1.02f else 1f
                scaleX = scale() * selectedScale
                scaleY = scale() * selectedScale
            },
        verticalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
        content = content,
    )
}

@Composable
private fun DockTabContent(
    @DrawableRes iconRes: Int,
    label: String,
    contentColor: Color,
    iconWidth: Dp = 27.dp,
    iconHeight: Dp = 27.dp,
) {
    Box(
        Modifier.size(27.dp),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier
                .size(width = iconWidth, height = iconHeight)
                .paint(
                    painter = painterResource(iconRes),
                    colorFilter = ColorFilter.tint(contentColor),
                ),
        )
    }
    BasicText(
        text = label,
        style = TextStyle(
            color = contentColor,
            fontSize = 11.sp,
            lineHeight = 12.sp,
        ),
    )
}

@Composable
internal fun LiquidMainDock(
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: Backdrop,
    actionEnabled: Boolean,
    actionBusy: Boolean,
    actionDescription: String,
    onAction: () -> Unit,
    modifier: Modifier = Modifier,
) {
    BoxWithConstraints(modifier) {
        val actionSize = 56.dp
        val navigationHeight = 56.dp
        val minimumGap = 16.dp
        val preferredNavigationWidth = (maxWidth * 0.525f).coerceIn(172.dp, 217.dp)
        val navigationWidth = minOf(preferredNavigationWidth, maxWidth - actionSize - minimumGap)
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            LiquidTabCapsule(
                selectedIndex,
                onSelected,
                backdrop,
                Modifier.size(width = navigationWidth, height = navigationHeight),
            )
            androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
            GlassIconButton(
                iconRes = R.drawable.ic_refresh,
                description = actionDescription,
                backdrop = backdrop,
                enabled = actionEnabled && !actionBusy,
                busy = actionBusy,
                buttonSize = actionSize,
                iconSize = 24.dp,
                onClick = onAction,
            )
        }
    }
}

@Composable
private fun LiquidTabCapsule(
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
) {
    val palette = LocalQuotaPalette.current
    val isLight = Color(palette.background).luminance() > 0.35f
    val containerColor = palette.color(palette.surface).copy(alpha = 0.4f)
    val accentColor = palette.color(palette.accent)
    val unselectedContentColor = if (isLight) {
        palette.color(palette.body)
    } else {
        Color(0xFFF1F3F7)
    }
    val tabsBackdrop = rememberLayerBackdrop()
    val scope = rememberCoroutineScope()
    val hapticFeedback = LocalHapticFeedback.current
    val debugLogging = LocalContext.current.applicationInfo.flags and
        ApplicationInfo.FLAG_DEBUGGABLE != 0

    BoxWithConstraints(modifier, contentAlignment = Alignment.CenterStart) {
        val density = LocalDensity.current
        val touchSlop = LocalViewConfiguration.current.touchSlop
        val tabWidth = with(density) { (constraints.maxWidth.toFloat() - 8.dp.toPx()) / 2f }
        val pressedScaleX = with(density) { (tabWidth + 22.dp.toPx()) / tabWidth }
        val offsetAnimation = remember { Animatable(0f) }
        val panelOffset by remember(density) {
            derivedStateOf {
                val fraction = (offsetAnimation.value / constraints.maxWidth).fastCoerceIn(-1f, 1f)
                with(density) { 4.dp.toPx() * fraction.sign * EaseOut.transform(abs(fraction)) }
            }
        }
        val isLtr = LocalLayoutDirection.current == LayoutDirection.Ltr
        var currentIndex by remember { mutableIntStateOf(selectedIndex) }
        var tapIndex by remember { mutableIntStateOf(selectedIndex) }
        var totalHorizontalDrag by remember { mutableStateOf(0f) }
        var gestureMoved by remember { mutableStateOf(false) }
        var settledLogJob by remember { mutableStateOf<Job?>(null) }
        val drag = remember(scope, hapticFeedback) {
            BottomDockDampedDragAnimation(
                animationScope = scope,
                initialValue = selectedIndex.toFloat(),
                valueRange = 0f..1f,
                visibilityThreshold = 0.001f,
                initialScale = 1f,
                pressedScaleX = pressedScaleX,
                pressedScaleY = 78f / 56f,
                onDragStarted = {
                    tapIndex = if (it.x < constraints.maxWidth / 2f) {
                        if (isLtr) 0 else 1
                    } else {
                        if (isLtr) 1 else 0
                    }
                    totalHorizontalDrag = 0f
                    gestureMoved = false
                    settledLogJob?.cancel()
                    logBottomDockState(debugLogging, "DOWN", currentIndex, this)
                },
                onDragStopped = {
                    settledLogJob?.cancel()
                    logBottomDockState(debugLogging, "UP", currentIndex, this)
                    val target = if (gestureMoved) {
                        targetValue.fastRoundToInt().fastCoerceIn(0, 1)
                    } else {
                        tapIndex
                    }
                    if (target != currentIndex) {
                        logBottomDockState(debugLogging, "HAPTIC", target, this)
                        hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                    }
                    currentIndex = target
                    settleToValue(target.toFloat())
                    totalHorizontalDrag = 0f
                    gestureMoved = false
                    scope.launch { offsetAnimation.animateTo(0f, spring(1f, 300f, 0.5f)) }
                    val animation = this
                    settledLogJob = scope.launch {
                        delay(600)
                        logBottomDockState(debugLogging, "SETTLED", currentIndex, animation)
                    }
                },
                onDragCancelled = {
                    settleToValue(currentIndex.toFloat())
                    totalHorizontalDrag = 0f
                    gestureMoved = false
                    scope.launch { offsetAnimation.animateTo(0f, spring(1f, 300f, 0.5f)) }
                },
                onDrag = { _, amount ->
                    totalHorizontalDrag += amount.x
                    val crossedTouchSlop = !gestureMoved && isDockDrag(totalHorizontalDrag, touchSlop)
                    if (crossedTouchSlop) {
                        gestureMoved = true
                        updateValue(
                            (targetValue + totalHorizontalDrag / tabWidth * if (isLtr) 1f else -1f)
                                .fastCoerceIn(0f, 1f),
                        )
                        scope.launch { offsetAnimation.snapTo(totalHorizontalDrag) }
                    } else if (gestureMoved) {
                        updateValue((targetValue + amount.x / tabWidth * if (isLtr) 1f else -1f).fastCoerceIn(0f, 1f))
                        scope.launch { offsetAnimation.snapTo(offsetAnimation.value + amount.x) }
                    }
                },
            )
        }
        LaunchedEffect(selectedIndex) {
            currentIndex = selectedIndex
        }
        LaunchedEffect(drag) {
            snapshotFlow { currentIndex }.drop(1).collectLatest { index ->
                drag.settleToValue(index.toFloat())
                onSelected(index)
            }
        }
        val interactiveHighlight = remember(scope) {
            GlassInteractiveHighlight(
                animationScope = scope,
                position = { size, _ ->
                    Offset(
                        if (isLtr) (drag.value + 0.5f) * tabWidth + panelOffset
                        else size.width - (drag.value + 0.5f) * tabWidth + panelOffset,
                        size.height / 2f,
                    )
                },
            )
        }

        val tabs: @Composable RowScope.() -> Unit = {
            DockTab(selected = currentIndex == 0, onSelect = { onSelected(0) }) {
                DockTabContent(
                    R.drawable.ic_quota_tray,
                    "额度",
                    unselectedContentColor,
                    iconWidth = 22.dp,
                    iconHeight = 24.dp,
                )
            }
            DockTab(selected = currentIndex == 1, onSelect = { onSelected(1) }) {
                DockTabContent(R.drawable.ic_usage, "统计", unselectedContentColor)
            }
        }
        Row(
            Modifier
                .graphicsLayer { translationX = panelOffset }
                .drawBackdrop(
                    backdrop, { KyantShapes.capsule() },
                    effects = { vibrancy(); blur(8.dp.toPx()); lens(24.dp.toPx(), 24.dp.toPx()) },
                    layerBlock = {
                        val scale = lerp(1f, 1f + 16.dp.toPx() / size.width, drag.pressProgress)
                        scaleX = scale; scaleY = scale
                    },
                    onDrawSurface = { drawRect(containerColor) },
                )
                .then(interactiveHighlight.modifier)
                .fillMaxHeight().fillMaxWidth().padding(4.dp),
            verticalAlignment = Alignment.CenterVertically,
            content = {},
        )
        Row(
            Modifier
                .graphicsLayer { translationX = panelOffset }
                .fillMaxHeight()
                .fillMaxWidth()
                .padding(4.dp)
                .drawWithContent {
                    val velocity = drag.velocity / 10f
                    val horizontalDeformation =
                        (velocity * 0.75f).fastCoerceIn(-0.2f, 0.2f)
                    val lensScaleX = drag.scaleX / (1f - horizontalDeformation)
                    val lensCenterX = if (isLtr) {
                        (drag.value + 0.5f) * tabWidth
                    } else {
                        size.width - (drag.value + 0.5f) * tabWidth
                    }
                    val lensHalfWidth = tabWidth * lensScaleX / 2f
                    val lensLeft = (lensCenterX - lensHalfWidth).fastCoerceIn(0f, size.width)
                    val lensRight = (lensCenterX + lensHalfWidth).fastCoerceIn(0f, size.width)
                    if (lensLeft > 0f) {
                        clipRect(right = lensLeft) { this@drawWithContent.drawContent() }
                    }
                    if (lensRight < size.width) {
                        clipRect(left = lensRight) { this@drawWithContent.drawContent() }
                    }
                },
            verticalAlignment = Alignment.CenterVertically,
            content = tabs,
        )
        CompositionLocalProvider(
            LocalDockTabScale provides { lerp(1f, 1.2f, drag.pressProgress) },
        ) {
            Row(
                Modifier.clearAndSetSemantics {}.alpha(0f).layerBackdrop(tabsBackdrop)
                    .graphicsLayer { translationX = panelOffset }
                    .drawBackdrop(
                        backdrop, { KyantShapes.capsule() },
                        effects = { vibrancy(); blur(8.dp.toPx()); lens(24.dp.toPx() * drag.pressProgress, 24.dp.toPx() * drag.pressProgress) },
                        highlight = { Highlight.Default.copy(alpha = drag.pressProgress) },
                        onDrawSurface = { drawRect(containerColor) },
                    )
                    .then(interactiveHighlight.modifier)
                    .fillMaxHeight().fillMaxWidth().padding(horizontal = 4.dp)
                    .graphicsLayer(colorFilter = ColorFilter.tint(accentColor)),
                verticalAlignment = Alignment.CenterVertically,
                content = tabs,
            )
        }
        Box(
            Modifier.padding(horizontal = 4.dp)
                .graphicsLayer { translationX = if (isLtr) drag.value * tabWidth + panelOffset else size.width - (drag.value + 1f) * tabWidth + panelOffset }
                .drawBackdrop(
                    rememberCombinedBackdrop(backdrop, tabsBackdrop), { KyantShapes.capsule() },
                    effects = { lens(10.dp.toPx() * drag.pressProgress, 14.dp.toPx() * drag.pressProgress, chromaticAberration = true) },
                    highlight = { Highlight.Default.copy(alpha = drag.pressProgress) },
                    shadow = { Shadow(alpha = drag.pressProgress) },
                    innerShadow = { InnerShadow(8.dp * drag.pressProgress, alpha = drag.pressProgress) },
                    layerBlock = {
                        scaleX = drag.scaleX; scaleY = drag.scaleY
                        val velocity = drag.velocity / 10f
                        scaleX /= 1f - (velocity * 0.75f).fastCoerceIn(-0.2f, 0.2f)
                        scaleY *= 1f - (velocity * 0.25f).fastCoerceIn(-0.2f, 0.2f)
                    },
                    onDrawSurface = {
                        drawRect(if (isLight) Color.Black.copy(0.1f) else Color.White.copy(0.1f), alpha = 1f - drag.pressProgress)
                        drawRect(Color.Black.copy(alpha = 0.03f * drag.pressProgress))
                    },
                )
                .fillMaxHeight().fillMaxWidth(0.5f),
        )
        Box(
            Modifier
                .fillMaxSize()
                .then(interactiveHighlight.gestureModifier)
                .then(drag.modifier),
        )
    }
}

private fun logBottomDockState(
    enabled: Boolean,
    event: String,
    currentIndex: Int,
    animation: BottomDockDampedDragAnimation,
) {
    if (!enabled) return
    Log.d(
        "BottomLiquidGlass",
        "event=$event currentIndex=$currentIndex value=${animation.value} " +
            "targetValue=${animation.targetValue} pressProgress=${animation.pressProgress} " +
            "scaleX=${animation.scaleX} scaleY=${animation.scaleY} velocity=${animation.velocity}",
    )
}
