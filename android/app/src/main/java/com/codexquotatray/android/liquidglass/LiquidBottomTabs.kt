// Adapted and modified from Kyant0/AndroidLiquidGlass.
// Pinned commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
package com.codexquotatray.android.liquidglass

import androidx.compose.animation.core.AnimationVector1D
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseOut
import androidx.compose.animation.core.spring
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.interaction.PressInteraction
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.util.fastCoerceIn
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
import com.kyant.shapes.Capsule
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.sign

@Composable
fun LiquidBottomTabs(
    selectedTabIndex: () -> Int,
    onTabSelected: (index: Int) -> Unit,
    backdrop: Backdrop,
    tabsCount: Int,
    indicatorRefractionHeight: Dp = 10.dp,
    indicatorRefractionAmount: Dp = 14.dp,
    modifier: Modifier = Modifier,
    content: @Composable RowScope.() -> Unit,
) {
    val isLightTheme = !isSystemInDarkTheme()
    val accentColor =
        if (isLightTheme) Color(0xFF0088FF)
        else Color(0xFF0091FF)
    val containerColor =
        if (isLightTheme) Color(0xFFFAFAFA).copy(0.4f)
        else Color(0xFF121212).copy(0.4f)

    val tabsBackdrop = rememberLayerBackdrop()

    BoxWithConstraints(
        modifier,
        contentAlignment = Alignment.CenterStart,
    ) {
        val density = LocalDensity.current
        val tabWidth = with(density) {
            (constraints.maxWidth.toFloat() - 8f.dp.toPx()) / tabsCount
        }

        val offsetAnimation = remember { Animatable(0f) }
        val panelOffset by remember(density) {
            derivedStateOf {
                val fraction = (offsetAnimation.value / constraints.maxWidth).fastCoerceIn(-1f, 1f)
                with(density) {
                    4f.dp.toPx() * fraction.sign * EaseOut.transform(abs(fraction))
                }
            }
        }

        val isLtr = LocalLayoutDirection.current == LayoutDirection.Ltr
        val animationScope = rememberCoroutineScope()
        var committedIndex by remember {
            mutableIntStateOf(selectedTabIndex().fastCoerceIn(0, tabsCount - 1))
        }
        var previewIndex by remember { mutableStateOf<Int?>(null) }
        var pendingCommitTarget by remember { mutableStateOf<Int?>(null) }
        var pendingCommitNotified by remember { mutableStateOf(false) }
        var pendingCommitFromDrag by remember { mutableStateOf(false) }
        var activePress by remember { mutableStateOf<PressInteraction.Press?>(null) }
        var activePressIndex by remember { mutableIntStateOf(-1) }
        var dragInProgress by remember { mutableStateOf(false) }
        var handoffDragIndex by remember { mutableIntStateOf(-1) }
        var handoffDragNeedsCurrentValue by remember { mutableStateOf(false) }
        val dampedDragAnimation = remember(animationScope) {
            DampedDragAnimation(
                animationScope = animationScope,
                initialValue = selectedTabIndex().toFloat(),
                valueRange = 0f..(tabsCount - 1).toFloat(),
                visibilityThreshold = 0.001f,
                initialScale = 1f,
                pressedScale = 78f / 56f,
                onDragStarted = {
                    dragInProgress = true
                    handoffDragIndex = -1
                    handoffDragNeedsCurrentValue = false
                    previewIndex = null
                    pendingCommitTarget = null
                    pendingCommitNotified = false
                    pendingCommitFromDrag = false
                },
                onDragStopped = {
                    val targetIndex = targetValue.fastRoundToInt().fastCoerceIn(0, tabsCount - 1)
                    dragInProgress = false
                    handoffDragIndex = -1
                    handoffDragNeedsCurrentValue = false
                    previewIndex = null
                    pendingCommitTarget = if (targetIndex != committedIndex) targetIndex else null
                    pendingCommitNotified = pendingCommitTarget != null
                    pendingCommitFromDrag = pendingCommitTarget != null
                    animateToValue(targetIndex.toFloat())
                    if (targetIndex != committedIndex) {
                        onTabSelected(targetIndex)
                    }
                    animationScope.launch {
                        offsetAnimation.animateTo(
                            0f,
                            spring(1f, 300f, 0.5f),
                        )
                    }
                },
                onDragCancelled = {
                    dragInProgress = false
                    handoffDragIndex = -1
                    handoffDragNeedsCurrentValue = false
                    activePress = null
                    activePressIndex = -1
                    previewIndex = null
                    pendingCommitTarget = null
                    pendingCommitNotified = false
                    pendingCommitFromDrag = false
                    settleToValue(committedIndex.toFloat())
                    animationScope.launch {
                        offsetAnimation.animateTo(
                            0f,
                            spring(1f, 300f, 0.5f),
                        )
                    }
                },
                onDrag = { _, dragAmount ->
                    applyBottomTabDragDelta(
                        dragAmountX = dragAmount.x,
                        tabWidth = tabWidth,
                        isLtr = isLtr,
                        tabsCount = tabsCount,
                        offsetAnimation = offsetAnimation,
                        animationScope = animationScope,
                    )
                },
            )
        }
        LaunchedEffect(selectedTabIndex) {
            snapshotFlow { selectedTabIndex() }
                .collectLatest { index ->
                    val committed = index.fastCoerceIn(0, tabsCount - 1)
                    if (committed == committedIndex) return@collectLatest

                    val isPendingCommit = pendingCommitTarget == committed
                    val isPreviewCommit = isPendingCommit && previewIndex == committed
                    val isPendingDragCommit = isPendingCommit && pendingCommitFromDrag
                    committedIndex = committed
                    previewIndex = null
                    pendingCommitTarget = null
                    pendingCommitNotified = false
                    pendingCommitFromDrag = false

                    if (isPreviewCommit) {
                        // The press already moved the pill. Only settle to the exact
                        // committed value; do not replay a full selection animation.
                        dampedDragAnimation.settleToValue(committed.toFloat())
                        if (activePress == null && !dragInProgress) {
                            dampedDragAnimation.release()
                        }
                    } else if (!isPendingDragCommit && !dragInProgress) {
                        // No active preview owns the visual target, so this is an
                        // external/programmatic selection and keeps the old behavior.
                        dampedDragAnimation.animateToValue(committed.toFloat())
                    }
                }
        }

        val interactionCallbacks = LiquidBottomTabInteractionCallbacks(
            onPress = { index, press ->
                if (activePress == null) {
                    activePress = press
                    activePressIndex = index
                    pendingCommitTarget = null
                    pendingCommitNotified = false
                    pendingCommitFromDrag = false
                    dampedDragAnimation.press()
                    if (index != committedIndex) {
                        previewIndex = index
                        val visualTargetIndex = previewIndex ?: committedIndex
                        dampedDragAnimation.settleToValue(visualTargetIndex.toFloat())
                    }
                }
            },
            onDragStart = { index ->
                if (
                    activePress != null &&
                    activePressIndex == index &&
                    previewIndex == index &&
                    !dragInProgress &&
                    handoffDragIndex == -1
                ) {
                    dragInProgress = true
                    handoffDragIndex = index
                    handoffDragNeedsCurrentValue = true
                    previewIndex = null
                    pendingCommitTarget = null
                    pendingCommitNotified = false
                    pendingCommitFromDrag = false
                    true
                } else {
                    false
                }
            },
            onDrag = { index, dragAmountX ->
                if (dragInProgress && handoffDragIndex == index) {
                    dampedDragAnimation.applyBottomTabDragDelta(
                        dragAmountX = dragAmountX,
                        tabWidth = tabWidth,
                        isLtr = isLtr,
                        tabsCount = tabsCount,
                        offsetAnimation = offsetAnimation,
                        animationScope = animationScope,
                        fromCurrentValue = handoffDragNeedsCurrentValue,
                    )
                    handoffDragNeedsCurrentValue = false
                }
            },
            onDragEnd = { index ->
                if (dragInProgress && handoffDragIndex == index) {
                    val targetIndex = dampedDragAnimation.targetValue
                        .fastRoundToInt()
                        .fastCoerceIn(0, tabsCount - 1)
                    dragInProgress = false
                    handoffDragIndex = -1
                    handoffDragNeedsCurrentValue = false
                    activePress = null
                    activePressIndex = -1
                    previewIndex = null
                    pendingCommitTarget = if (targetIndex != committedIndex) targetIndex else null
                    pendingCommitNotified = pendingCommitTarget != null
                    pendingCommitFromDrag = pendingCommitTarget != null
                    dampedDragAnimation.settleToValue(targetIndex.toFloat())
                    dampedDragAnimation.release()
                    if (targetIndex != committedIndex) {
                        onTabSelected(targetIndex)
                    }
                    animationScope.launch {
                        offsetAnimation.animateTo(
                            0f,
                            spring(1f, 300f, 0.5f),
                        )
                    }
                }
            },
            onDragCancel = { index ->
                if (dragInProgress && handoffDragIndex == index) {
                    dragInProgress = false
                    handoffDragIndex = -1
                    handoffDragNeedsCurrentValue = false
                    activePress = null
                    activePressIndex = -1
                    previewIndex = null
                    pendingCommitTarget = null
                    pendingCommitNotified = false
                    pendingCommitFromDrag = false
                    dampedDragAnimation.settleToValue(committedIndex.toFloat())
                    dampedDragAnimation.release()
                    animationScope.launch {
                        offsetAnimation.animateTo(
                            0f,
                            spring(1f, 300f, 0.5f),
                        )
                    }
                }
            },
            onRelease = { index, press ->
                if (activePress === press && activePressIndex == index) {
                    val isHandoffDrag = dragInProgress && handoffDragIndex == index
                    activePress = null
                    activePressIndex = -1
                    if (!isHandoffDrag) {
                        if (index == committedIndex) {
                            previewIndex = null
                            pendingCommitTarget = null
                            pendingCommitNotified = false
                            pendingCommitFromDrag = false
                            dampedDragAnimation.release()
                        } else if (previewIndex == index) {
                            pendingCommitTarget = index
                            pendingCommitNotified = false
                            pendingCommitFromDrag = false
                        } else {
                            pendingCommitTarget = null
                            pendingCommitNotified = false
                            pendingCommitFromDrag = false
                            dampedDragAnimation.release()
                        }
                    }
                }
            },
            onCancel = { index, press ->
                if (activePress === press && activePressIndex == index) {
                    val isHandoffDrag = dragInProgress && handoffDragIndex == index
                    activePress = null
                    activePressIndex = -1
                    if (!isHandoffDrag) {
                        val wasPreview = previewIndex == index
                        previewIndex = null
                        pendingCommitTarget = null
                        pendingCommitNotified = false
                        pendingCommitFromDrag = false
                        if (wasPreview) {
                            dampedDragAnimation.settleToValue(committedIndex.toFloat())
                        }
                        dampedDragAnimation.release()
                    }
                }
            },
            onClick = { index ->
                val targetIndex = index.fastCoerceIn(0, tabsCount - 1)
                if (
                    targetIndex != committedIndex &&
                    !pendingCommitNotified &&
                    !dragInProgress &&
                    handoffDragIndex == -1
                ) {
                    pendingCommitTarget = targetIndex
                    pendingCommitNotified = true
                    pendingCommitFromDrag = false
                    onTabSelected(targetIndex)
                }
            },
        )

        val interactiveHighlight = remember(animationScope) {
            InteractiveHighlight(
                animationScope = animationScope,
                position = { size, _ ->
                    Offset(
                        if (isLtr) (dampedDragAnimation.value + 0.5f) * tabWidth + panelOffset
                        else size.width - (dampedDragAnimation.value + 0.5f) * tabWidth + panelOffset,
                        size.height / 2f,
                    )
                },
            )
        }

        CompositionLocalProvider(
            LocalLiquidBottomTabInteraction provides interactionCallbacks,
        ) {
            Row(
                Modifier
                    .graphicsLayer {
                        translationX = panelOffset
                    }
                    .drawBackdrop(
                        backdrop = backdrop,
                        shape = { Capsule() },
                        effects = {
                            vibrancy()
                            blur(8f.dp.toPx())
                            lens(24f.dp.toPx(), 24f.dp.toPx())
                        },
                        layerBlock = {
                            val progress = dampedDragAnimation.pressProgress
                            val scale = lerp(1f, 1f + 16f.dp.toPx() / size.width, progress)
                            scaleX = scale
                            scaleY = scale
                        },
                        onDrawSurface = { drawRect(containerColor) },
                    )
                    .then(interactiveHighlight.modifier)
                    .height(64f.dp)
                    .fillMaxWidth()
                    .padding(4f.dp),
                verticalAlignment = Alignment.CenterVertically,
                content = content,
            )

            CompositionLocalProvider(
                LocalLiquidBottomTabScale provides {
                    lerp(1f, 1.2f, dampedDragAnimation.pressProgress)
                },
            ) {
                Row(
                    Modifier
                        .clearAndSetSemantics {}
                        .alpha(0f)
                        .layerBackdrop(tabsBackdrop)
                        .graphicsLayer {
                            translationX = panelOffset
                        }
                        .drawBackdrop(
                            backdrop = backdrop,
                            shape = { Capsule() },
                            effects = {
                                val progress = dampedDragAnimation.pressProgress
                                vibrancy()
                                blur(8f.dp.toPx())
                                lens(
                                    24f.dp.toPx() * progress,
                                    24f.dp.toPx() * progress,
                                )
                            },
                            highlight = {
                                val progress = dampedDragAnimation.pressProgress
                                Highlight.Default.copy(alpha = progress)
                            },
                            onDrawSurface = { drawRect(containerColor) },
                        )
                        .then(interactiveHighlight.modifier)
                        .height(56f.dp)
                        .fillMaxWidth()
                        .padding(horizontal = 4f.dp)
                        .graphicsLayer(colorFilter = ColorFilter.tint(accentColor)),
                    verticalAlignment = Alignment.CenterVertically,
                    content = content,
                )
            }
        }

        Box(
            Modifier
                .padding(horizontal = 4f.dp)
                .graphicsLayer {
                    translationX =
                        if (isLtr) dampedDragAnimation.value * tabWidth + panelOffset
                        else size.width - (dampedDragAnimation.value + 1f) * tabWidth + panelOffset
                }
                .then(interactiveHighlight.gestureModifier)
                .then(dampedDragAnimation.modifier)
                .drawBackdrop(
                    backdrop = rememberCombinedBackdrop(backdrop, tabsBackdrop),
                    shape = { Capsule() },
                    effects = {
                        val progress = dampedDragAnimation.pressProgress
                        lens(
                            indicatorRefractionHeight.toPx() * progress,
                            indicatorRefractionAmount.toPx() * progress,
                            chromaticAberration = true,
                        )
                    },
                    highlight = {
                        val progress = dampedDragAnimation.pressProgress
                        Highlight.Default.copy(alpha = progress)
                    },
                    shadow = {
                        val progress = dampedDragAnimation.pressProgress
                        Shadow(alpha = progress)
                    },
                    innerShadow = {
                        val progress = dampedDragAnimation.pressProgress
                        InnerShadow(
                            radius = 8f.dp * progress,
                            alpha = progress,
                        )
                    },
                    layerBlock = {
                        scaleX = dampedDragAnimation.scaleX
                        scaleY = dampedDragAnimation.scaleY
                        val velocity = dampedDragAnimation.velocity / 10f
                        scaleX /= 1f - (velocity * 0.75f).fastCoerceIn(-0.2f, 0.2f)
                        scaleY *= 1f - (velocity * 0.25f).fastCoerceIn(-0.2f, 0.2f)
                    },
                    onDrawSurface = {
                        val progress = dampedDragAnimation.pressProgress
                        drawRect(
                            if (isLightTheme) Color.Black.copy(0.1f)
                            else Color.White.copy(0.1f),
                            alpha = 1f - progress,
                        )
                        drawRect(Color.Black.copy(alpha = 0.03f * progress))
                    },
                )
                .height(56f.dp)
                .fillMaxWidth(1f / tabsCount),
        )
    }
}

private fun DampedDragAnimation.applyBottomTabDragDelta(
    dragAmountX: Float,
    tabWidth: Float,
    isLtr: Boolean,
    tabsCount: Int,
    offsetAnimation: Animatable<Float, AnimationVector1D>,
    animationScope: CoroutineScope,
    fromCurrentValue: Boolean = false,
) {
    val baseValue = if (fromCurrentValue) value else targetValue
    updateValue(
        (baseValue + dragAmountX / tabWidth * if (isLtr) 1f else -1f)
            .fastCoerceIn(0f, (tabsCount - 1).toFloat()),
    )
    animationScope.launch {
        offsetAnimation.snapTo(offsetAnimation.value + dragAmountX)
    }
}
