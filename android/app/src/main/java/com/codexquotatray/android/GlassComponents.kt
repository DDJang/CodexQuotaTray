package com.codexquotatray.android

import android.content.pm.ApplicationInfo
import android.util.Log

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseOut
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
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
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.TextStyle
import androidx.annotation.DrawableRes
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.fastFirstOrNull
import androidx.compose.ui.util.fastRoundToInt
import androidx.compose.ui.util.lerp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberCombinedBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
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
import kotlin.math.sign

@Composable
internal fun GlassIconButton(
    @DrawableRes iconRes: Int,
    description: String,
    backdrop: Backdrop,
    enabled: Boolean = true,
    busy: Boolean = false,
    size: Dp = 52.dp,
    iconSize: Dp = 24.dp,
    onClick: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnClick = rememberSystemHapticClick(onClick)
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val pressProgress by animateFloatAsState(
        if (pressed) 1f else 0f,
        spring(dampingRatio = 0.62f, stiffness = 520f),
        label = "glass-button-press",
    )
    Box(
        Modifier
            .size(size)
            .drawBackdrop(
                backdrop = backdrop,
                shape = { KyantShapes.capsule() },
                effects = {
                    vibrancy()
                    blur(7.dp.toPx())
                    lens(
                        11.dp.toPx() * (0.8f + 0.2f * pressProgress),
                        10.dp.toPx(),
                        chromaticAberration = false,
                    )
                },
                highlight = { Highlight.Default.copy(alpha = 0.7f) },
                shadow = { Shadow(alpha = 0.55f) },
                innerShadow = { InnerShadow(radius = 5.dp, alpha = 0.5f) },
                layerBlock = {
                    val scale = lerp(1f, 0.9f, pressProgress)
                    scaleX = scale
                    scaleY = scale
                },
                onDrawSurface = { drawRect(palette.color(palette.surface).copy(alpha = 0.2f)) },
            )
            .clip(KyantShapes.capsule())
            .clickable(
                interactionSource = interaction,
                indication = null,
                enabled = enabled,
                role = Role.Button,
                onClick = hapticOnClick,
            )
            .semantics { contentDescription = description }
            .alpha(if (enabled) 1f else 0.45f),
        contentAlignment = Alignment.Center,
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
private fun RowScope.DockTab(onClick: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    val scale = LocalDockTabScale.current
    val hapticOnClick = rememberSystemHapticClick(onClick)
    Column(
        Modifier
            .clip(KyantShapes.capsule())
            .clickable(interactionSource = null, indication = null, role = Role.Tab, onClick = hapticOnClick)
            .fillMaxHeight()
            .weight(1f)
            .graphicsLayer { scaleX = scale(); scaleY = scale() },
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
) {
    Box(
        Modifier
            .size(27.dp)
            .paint(
                painter = painterResource(iconRes),
                colorFilter = ColorFilter.tint(contentColor),
            ),
    )
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
    onAction: () -> Unit,
    modifier: Modifier = Modifier,
) {
    BoxWithConstraints(modifier) {
        val actionSize = 56.dp
        val minimumGap = 16.dp
        val preferredNavigationWidth = (maxWidth * 0.60f).coerceIn(196.dp, 248.dp)
        val navigationWidth = minOf(preferredNavigationWidth, maxWidth - actionSize - minimumGap)
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            LiquidTabCapsule(
                selectedIndex,
                onSelected,
                backdrop,
                Modifier.size(width = navigationWidth, height = 64.dp),
            )
            androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
            GlassIconButton(
                iconRes = R.drawable.ic_refresh,
                description = "刷新当前页面",
                backdrop = backdrop,
                enabled = actionEnabled && !actionBusy,
                busy = actionBusy,
                size = actionSize,
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
                    settledLogJob?.cancel()
                    logBottomDockState(debugLogging, "DOWN", currentIndex, this)
                },
                onDragStopped = {
                    settledLogJob?.cancel()
                    logBottomDockState(debugLogging, "UP", currentIndex, this)
                    val target = targetValue.fastRoundToInt().fastCoerceIn(0, 1)
                    if (target != currentIndex) {
                        logBottomDockState(debugLogging, "HAPTIC", target, this)
                        hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                    }
                    currentIndex = target
                    settleToValue(target.toFloat())
                    scope.launch { offsetAnimation.animateTo(0f, spring(1f, 300f, 0.5f)) }
                    val animation = this
                    settledLogJob = scope.launch {
                        delay(600)
                        logBottomDockState(debugLogging, "SETTLED", currentIndex, animation)
                    }
                },
                onDrag = { _, amount ->
                    updateValue((targetValue + amount.x / tabWidth * if (isLtr) 1f else -1f).fastCoerceIn(0f, 1f))
                    scope.launch { offsetAnimation.snapTo(offsetAnimation.value + amount.x) }
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
            BottomDockInteractiveHighlight(
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
            DockTab({ currentIndex = 0 }) {
                DockTabContent(R.drawable.ic_quota, "额度", unselectedContentColor)
            }
            DockTab({ currentIndex = 1 }) {
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
                .height(64.dp).fillMaxWidth().padding(4.dp),
            verticalAlignment = Alignment.CenterVertically,
            content = {},
        )
        Row(
            Modifier
                .graphicsLayer { translationX = panelOffset }
                .height(64.dp)
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
                    .height(56.dp).fillMaxWidth().padding(horizontal = 4.dp)
                    .graphicsLayer(colorFilter = ColorFilter.tint(accentColor)),
                verticalAlignment = Alignment.CenterVertically,
                content = tabs,
            )
        }
        Box(
            Modifier.padding(horizontal = 4.dp)
                .graphicsLayer { translationX = if (isLtr) drag.value * tabWidth + panelOffset else size.width - (drag.value + 1f) * tabWidth + panelOffset }
                .then(interactiveHighlight.gestureModifier)
                .then(drag.modifier)
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
                .height(56.dp).fillMaxWidth(0.5f),
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
