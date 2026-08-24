// Adapted and modified from Kyant0/AndroidLiquidGlass.
// Debug-only adaptation of Kyant0/AndroidLiquidGlass LiquidBottomTabs.
// Pinned commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
//
// Do not wrap the upstream component in an expanded logical width and then
// scale it back down. Upstream translation and backdrop sampling must use the
// same real constraints as the visible and gesture coordinate spaces.
package com.codexquotatray.android.debug

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseOut
import androidx.compose.animation.core.spring
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.fastRoundToInt
import androidx.compose.ui.util.lerp
import com.codexquotatray.android.liquidglass.DampedDragAnimation
import com.codexquotatray.android.liquidglass.InteractiveHighlight
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
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.sign

private const val SEGMENTED_SCALE = 0.75f

private val segmentedOuterHeight = 64.dp.scaledSegmented()
private val segmentedSelectedHeight = 56.dp.scaledSegmented()
private val segmentedOuterPadding = 4.dp.scaledSegmented()
private val segmentedPanelOffset = 4.dp.scaledSegmented()
private val segmentedBlurRadius = 8.dp.scaledSegmented()
private val segmentedLensSize = 24.dp.scaledSegmented()
private val segmentedSelectedLensWidth = 10.dp.scaledSegmented()
private val segmentedSelectedLensHeight = 14.dp.scaledSegmented()
private val segmentedInnerShadowRadius = 8.dp.scaledSegmented()
private val segmentedOuterPressDeformation = 16.dp.scaledSegmented()
private val segmentedTabContentSpacing = 2.dp.scaledSegmented()

private fun Dp.scaledSegmented(): Dp = this * SEGMENTED_SCALE

private val LocalKyantSegmentedTabScale = staticCompositionLocalOf { { 1f } }

@Composable
internal fun KyantLiquidSegmentedAdaptation(
    labels: List<String>,
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: Backdrop,
    contentColor: Color = Color.White,
    modifier: Modifier = Modifier,
) {
    var requestedIndex by remember { mutableIntStateOf(selectedIndex) }
    LaunchedEffect(selectedIndex) {
        requestedIndex = selectedIndex
    }

    val requestedIndexState = rememberUpdatedState(requestedIndex)
    val committedSelectedIndex = rememberUpdatedState(selectedIndex)
    val selectionSink = rememberUpdatedState(onSelected)
    val selectedProvider = remember { { requestedIndexState.value } }
    val hapticFeedback = LocalHapticFeedback.current
    val stableSelectionSink = remember {
        { index: Int ->
            if (committedSelectedIndex.value != index) {
                hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                selectionSink.value(index)
            }
        }
    }
    val tabsCount = labels.size
    val isLightTheme = !isSystemInDarkTheme()
    val accentColor =
        if (isLightTheme) Color(0xFF0088FF)
        else Color(0xFF0091FF)
    val containerColor =
        if (isLightTheme) Color(0xFFFAFAFA).copy(0.4f)
        else Color(0xFF121212).copy(0.4f)

    val tabsBackdrop = rememberLayerBackdrop()

    BoxWithConstraints(
        modifier
            .fillMaxWidth()
            .height(segmentedOuterHeight),
        contentAlignment = Alignment.CenterStart,
    ) {
        val density = LocalDensity.current
        val outerPaddingPx = with(density) { (segmentedOuterPadding * 2f).toPx() }
        val tabWidth = (constraints.maxWidth.toFloat() - outerPaddingPx) / tabsCount

        val offsetAnimation = remember { Animatable(0f) }
        val panelOffset by remember(density) {
            derivedStateOf {
                val fraction = (offsetAnimation.value / constraints.maxWidth).fastCoerceIn(-1f, 1f)
                with(density) {
                    segmentedPanelOffset.toPx() * fraction.sign * EaseOut.transform(abs(fraction))
                }
            }
        }

        val isLtr = LocalLayoutDirection.current == LayoutDirection.Ltr
        val animationScope = rememberCoroutineScope()
        var currentIndex by remember(selectedProvider) {
            mutableIntStateOf(selectedProvider())
        }
        val dampedDragAnimation = remember(animationScope) {
            DampedDragAnimation(
                animationScope = animationScope,
                initialValue = selectedProvider().toFloat(),
                valueRange = 0f..(tabsCount - 1).toFloat(),
                visibilityThreshold = 0.001f,
                initialScale = 1f,
                pressedScale = 78f / 56f,
                onDragStarted = {},
                onDragStopped = {
                    val targetIndex = targetValue.fastRoundToInt().fastCoerceIn(0, tabsCount - 1)
                    currentIndex = targetIndex
                    animateToValue(targetIndex.toFloat())
                    animationScope.launch {
                        offsetAnimation.animateTo(
                            0f,
                            spring(1f, 300f, 0.5f),
                        )
                    }
                },
                onDrag = { _, dragAmount ->
                    updateValue(
                        (targetValue + dragAmount.x / tabWidth * if (isLtr) 1f else -1f)
                            .fastCoerceIn(0f, (tabsCount - 1).toFloat()),
                    )
                    animationScope.launch {
                        offsetAnimation.snapTo(offsetAnimation.value + dragAmount.x)
                    }
                },
            )
        }
        LaunchedEffect(selectedProvider) {
            snapshotFlow { selectedProvider() }
                .collectLatest { index ->
                    currentIndex = index
                }
        }
        LaunchedEffect(dampedDragAnimation) {
            snapshotFlow { currentIndex }
                .drop(1)
                .collectLatest { index ->
                    dampedDragAnimation.animateToValue(index.toFloat())
                    stableSelectionSink(index)
                }
        }

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
                        blur(with(density) { segmentedBlurRadius.toPx() })
                        lens(
                            with(density) { segmentedLensSize.toPx() },
                            with(density) { segmentedLensSize.toPx() },
                        )
                    },
                    layerBlock = {
                        val progress = dampedDragAnimation.pressProgress
                        val scale = lerp(
                            1f,
                            1f + with(density) { segmentedOuterPressDeformation.toPx() } / size.width,
                            progress,
                        )
                        scaleX = scale
                        scaleY = scale
                    },
                    onDrawSurface = { drawRect(containerColor) },
                )
                .then(interactiveHighlight.modifier)
                .height(segmentedOuterHeight)
                .fillMaxWidth()
                .padding(segmentedOuterPadding),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            labels.forEachIndexed { index, label ->
                KyantLiquidSegmentedTab(onClick = { requestedIndex = index }) {
                    androidx.compose.material3.Text(
                        text = label,
                        color = contentColor,
                        fontSize = 14.sp,
                        maxLines = 1,
                    )
                }
            }
        }

        CompositionLocalProvider(
            LocalKyantSegmentedTabScale provides {
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
                            blur(with(density) { segmentedBlurRadius.toPx() })
                            lens(
                                with(density) { segmentedLensSize.toPx() } * progress,
                                with(density) { segmentedLensSize.toPx() } * progress,
                            )
                        },
                        highlight = {
                            val progress = dampedDragAnimation.pressProgress
                            Highlight.Default.copy(alpha = progress)
                        },
                        onDrawSurface = { drawRect(containerColor) },
                    )
                    .then(interactiveHighlight.modifier)
                    .height(segmentedSelectedHeight)
                    .fillMaxWidth()
                    .padding(horizontal = segmentedOuterPadding)
                    .graphicsLayer(colorFilter = ColorFilter.tint(accentColor)),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                labels.forEachIndexed { index, label ->
                    KyantLiquidSegmentedTab(onClick = { requestedIndex = index }) {
                        androidx.compose.material3.Text(
                            text = label,
                            color = contentColor,
                            fontSize = 14.sp,
                            maxLines = 1,
                        )
                    }
                }
            }
        }

        Box(
            Modifier
                .padding(horizontal = segmentedOuterPadding)
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
                            with(density) { segmentedSelectedLensWidth.toPx() } * progress,
                            with(density) { segmentedSelectedLensHeight.toPx() } * progress,
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
                            radius = segmentedInnerShadowRadius * progress,
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
                .height(segmentedSelectedHeight)
                .fillMaxWidth(1f / tabsCount),
        )
    }
}

@Composable
private fun RowScope.KyantLiquidSegmentedTab(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val scale = LocalKyantSegmentedTabScale.current
    Column(
        modifier
            .clip(Capsule())
            .clickable(
                interactionSource = null,
                indication = null,
                role = Role.Tab,
                onClick = onClick,
            )
            .fillMaxHeight()
            .weight(1f)
            .graphicsLayer {
                val scale = scale()
                scaleX = scale
                scaleY = scale
            },
        verticalArrangement = Arrangement.spacedBy(
            segmentedTabContentSpacing,
            Alignment.CenterVertically,
        ),
        horizontalAlignment = Alignment.CenterHorizontally,
        content = content,
    )
}
