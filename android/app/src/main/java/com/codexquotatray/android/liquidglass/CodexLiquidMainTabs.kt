// Adapted and extensively modified from Kyant0/AndroidLiquidGlass.
// Reference implementation commit: b18eb0ff12c616546a68c72e7d0097f1ab286c87.
// Apache License 2.0.
package com.codexquotatray.android.liquidglass

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.changedToUpIgnoreConsumed
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.lerp
import com.codexquotatray.android.R
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.RuntimeShader
import com.kyant.backdrop.asComposeShader
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.isRuntimeShaderSupported
import com.kyant.backdrop.shadow.InnerShadow
import com.kyant.backdrop.shadow.Shadow
import com.kyant.shapes.Capsule
import kotlin.math.abs

@Composable
internal fun CodexLiquidMainTabs(
    selectedIndex: Int,
    onSelected: (Int) -> Unit,
    backdrop: Backdrop,
    contentColor: Color,
    modifier: Modifier = Modifier,
) {
    val isLightTheme = !isSystemInDarkTheme()
    val accentColor = if (isLightTheme) Color(0xFF0088FF) else Color(0xFF0091FF)
    val containerColor =
        if (isLightTheme) Color(0xFFFAFAFA).copy(alpha = 0.4f)
        else Color(0xFF121212).copy(alpha = 0.4f)
    val scope = rememberCoroutineScope()
    val motion = remember(scope) { CodexLiquidDockMotionState(selectedIndex, scope) }
    val selectedState = rememberUpdatedState(selectedIndex.coerceIn(0, 1))
    val onSelectedState = rememberUpdatedState(onSelected)
    val haptic = LocalHapticFeedback.current
    val isLeftToRight = LocalLayoutDirection.current == LayoutDirection.Ltr

    fun commit(index: Int?) {
        if (index == null) return
        haptic.performHapticFeedback(HapticFeedbackType.ContextClick)
        onSelectedState.value(index)
    }

    LaunchedEffect(selectedIndex) {
        motion.syncExternalSelection(selectedIndex)
    }

    BoxWithConstraints(
        modifier = modifier,
        contentAlignment = Alignment.CenterStart,
    ) {
        val density = LocalDensity.current
        val outerWidthPx = constraints.maxWidth.toFloat()
        val horizontalPaddingPx = with(density) { 4.dp.toPx() }
        val innerWidthPx = (outerWidthPx - horizontalPaddingPx * 2f).coerceAtLeast(0f)
        val tabWidthPx = innerWidthPx / 2f
        val tabWidth = with(density) { tabWidthPx.toDp() }
        val innerWidth = with(density) { innerWidthPx.toDp() }
        val panelOffset = with(density) {
            val displacement = motion.visualPosition - selectedState.value.toFloat()
            4.dp.toPx() * displacement.fastCoerceIn(-1f, 1f)
        }
        val selectorOffset = mainDockSelectorOffset(
            position = motion.visualPosition,
            availableWidth = tabWidthPx,
            isLeftToRight = isLeftToRight,
        )
        val highlight = codexSelectorCenterHighlight(
            progress = { motion.pressProgress },
            center = {
                Offset(
                    x = horizontalPaddingPx + selectorOffset + tabWidthPx / 2f + panelOffset,
                    y = it.height / 2f,
                )
            },
        )

        Box(
            Modifier
                .fillMaxSize()
                .graphicsLayer { translationX = panelOffset }
                .drawBackdrop(
                    backdrop = backdrop,
                    shape = { Capsule() },
                    effects = {
                        vibrancy()
                        blur(8.dp.toPx())
                        lens(24.dp.toPx(), 24.dp.toPx())
                    },
                    layerBlock = {
                        val scale = lerp(1f, 1f + 16.dp.toPx() / size.width, motion.pressProgress)
                        scaleX = scale
                        scaleY = scale
                    },
                    onDrawSurface = { drawRect(containerColor) },
                )
                .then(highlight)
                .height(64.dp)
                .padding(4.dp),
        ) {
            MainDockTabRow(
                selectedIndex = selectedState.value,
                contentColor = contentColor,
                pressScale = lerp(1f, 1.2f, motion.pressProgress),
                onSemanticSelection = { requested ->
                    val generation = motion.beginGesture()
                    commit(motion.finishTap(generation, requested))
                },
                modifier = Modifier.fillMaxSize(),
            )
        }

        Box(
            Modifier
                .padding(start = 4.dp)
                .width(tabWidth)
                .height(56.dp)
                .graphicsLayer {
                    translationX = selectorOffset + panelOffset
                    val pressedScale = lerp(1f, 78f / 56f, motion.pressProgress)
                    val velocity = (motion.normalizedVelocity / 10f).fastCoerceIn(-0.2f, 0.2f)
                    scaleX = pressedScale / (1f - velocity * 0.75f)
                    scaleY = pressedScale * (1f - velocity * 0.25f)
                }
                .drawBackdrop(
                    backdrop = backdrop,
                    shape = { Capsule() },
                    effects = {
                        val progress = motion.pressProgress
                        lens(
                            10.dp.toPx() * progress,
                            14.dp.toPx() * progress,
                            chromaticAberration = true,
                        )
                    },
                    highlight = { Highlight.Default.copy(alpha = motion.pressProgress) },
                    shadow = { Shadow(alpha = motion.pressProgress) },
                    innerShadow = {
                        InnerShadow(
                            radius = 8.dp * motion.pressProgress,
                            alpha = motion.pressProgress,
                        )
                    },
                    onDrawSurface = {
                        val progress = motion.pressProgress
                        drawRect(
                            if (isLightTheme) Color.Black.copy(alpha = 0.1f)
                            else Color.White.copy(alpha = 0.1f),
                            alpha = 1f - progress,
                        )
                        drawRect(Color.Black.copy(alpha = 0.03f * progress))
                    },
                )
                .clip(Capsule()),
        ) {
            MainDockTabRow(
                selectedIndex = selectedState.value,
                contentColor = contentColor,
                pressScale = lerp(1f, 1.2f, motion.pressProgress),
                onSemanticSelection = {},
                modifier = Modifier
                    .clearAndSetSemantics {}
                    .wrapContentSize(Alignment.TopStart, unbounded = true)
                    .requiredWidth(innerWidth)
                    .fillMaxHeight()
                    .graphicsLayer {
                        translationX = -selectorOffset
                        colorFilter = ColorFilter.tint(accentColor)
                    },
                exposeSemantics = false,
            )
        }

        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(motion, tabWidthPx, isLeftToRight) {
                    awaitEachGesture {
                        val down = awaitFirstDown(requireUnconsumed = false)
                        val generation = motion.beginGesture()
                        val startingPosition = motion.visualPosition
                        val tracker = VelocityTracker()
                        tracker.addPosition(down.uptimeMillis, down.position)
                        var dragged = false
                        var cancelled = false

                        while (true) {
                            val event = awaitPointerEvent()
                            val change = event.changes.firstOrNull { it.id == down.id }
                            if (change == null || change.isConsumed) {
                                cancelled = true
                                break
                            }
                            tracker.addPosition(change.uptimeMillis, change.position)
                            val physicalDelta = change.position.x - down.position.x
                            if (!dragged && abs(physicalDelta) > viewConfiguration.touchSlop) {
                                dragged = true
                            }
                            if (dragged && tabWidthPx > 0f) {
                                val logicalDelta = mainDockPositionDelta(
                                    physicalDeltaFraction = physicalDelta / tabWidthPx,
                                    isLeftToRight = isLeftToRight,
                                )
                                val physicalVelocity = tracker.calculateVelocity().x / tabWidthPx
                                motion.dragTo(
                                    generation = generation,
                                    position = startingPosition + logicalDelta,
                                    velocity = mainDockPositionDelta(physicalVelocity, isLeftToRight),
                                )
                                change.consume()
                            }
                            if (change.changedToUpIgnoreConsumed()) break
                        }

                        when {
                            cancelled -> motion.cancelGesture(generation)
                            dragged -> commit(motion.finishDrag(generation, cancelled = false)?.committedIndex)
                            else -> {
                                val tapped = mainDockTabAtPhysicalPosition(
                                    positionFraction = down.position.x / outerWidthPx,
                                    isLeftToRight = isLeftToRight,
                                )
                                commit(motion.finishTap(generation, tapped))
                            }
                        }
                    }
                },
        )
    }
}

@Composable
private fun MainDockTabRow(
    selectedIndex: Int,
    contentColor: Color,
    pressScale: Float,
    onSemanticSelection: (Int) -> Unit,
    modifier: Modifier,
    exposeSemantics: Boolean = true,
) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        MainDockTab(
            index = 0,
            selectedIndex = selectedIndex,
            iconRes = R.drawable.ic_quota_tray,
            label = "额度",
            contentColor = contentColor,
            iconWidth = 22.dp,
            iconHeight = 24.dp,
            pressScale = pressScale,
            exposeSemantics = exposeSemantics,
            onSemanticSelection = onSemanticSelection,
        )
        MainDockTab(
            index = 1,
            selectedIndex = selectedIndex,
            iconRes = R.drawable.ic_usage,
            label = "统计",
            contentColor = contentColor,
            iconWidth = 27.dp,
            iconHeight = 27.dp,
            pressScale = pressScale,
            exposeSemantics = exposeSemantics,
            onSemanticSelection = onSemanticSelection,
        )
    }
}

@Composable
private fun androidx.compose.foundation.layout.RowScope.MainDockTab(
    index: Int,
    selectedIndex: Int,
    iconRes: Int,
    label: String,
    contentColor: Color,
    iconWidth: androidx.compose.ui.unit.Dp,
    iconHeight: androidx.compose.ui.unit.Dp,
    pressScale: Float,
    exposeSemantics: Boolean,
    onSemanticSelection: (Int) -> Unit,
) {
    val semanticsModifier = if (exposeSemantics) {
        Modifier.semantics {
            role = Role.Tab
            selected = selectedIndex == index
            onClick(label = label) {
                onSemanticSelection(index)
                true
            }
        }
    } else {
        Modifier
    }
    Column(
        modifier = Modifier
            .weight(1f)
            .fillMaxHeight()
            .then(semanticsModifier)
            .graphicsLayer {
                scaleX = pressScale
                scaleY = pressScale
            },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterVertically),
    ) {
        androidx.compose.foundation.Image(
            painter = painterResource(iconRes),
            contentDescription = null,
            modifier = Modifier.size(width = iconWidth, height = iconHeight),
            colorFilter = ColorFilter.tint(contentColor),
        )
        BasicText(
            text = label,
            style = TextStyle(color = contentColor, fontSize = 11.sp, lineHeight = 12.sp),
        )
    }
}

@Composable
private fun codexSelectorCenterHighlight(
    progress: () -> Float,
    center: (androidx.compose.ui.geometry.Size) -> Offset,
): Modifier {
    val shader = remember {
        if (isRuntimeShaderSupported()) {
            RuntimeShader(
                """
uniform float2 size;
layout(color) uniform half4 color;
uniform float radius;
uniform float2 position;

half4 main(float2 coord) {
    float dist = distance(coord, position);
    float intensity = smoothstep(radius, radius * 0.5, dist);
    return color * intensity;
}""",
            )
        } else {
            null
        }
    }
    return Modifier.drawWithContent {
        val value = progress().coerceIn(0f, 1f)
        if (value > 0f) {
            if (shader != null) {
                drawRect(Color.White.copy(alpha = 0.08f * value), blendMode = BlendMode.Plus)
                val highlightCenter = center(size)
                shader.setFloatUniform("size", size.width, size.height)
                shader.setColorUniform("color", Color.White.copy(alpha = 0.15f * value))
                shader.setFloatUniform("radius", size.minDimension * 1.5f)
                shader.setFloatUniform("position", highlightCenter.x, highlightCenter.y)
                drawRect(ShaderBrush(shader.asComposeShader()), blendMode = BlendMode.Plus)
            } else {
                drawRect(Color.White.copy(alpha = 0.25f * value), blendMode = BlendMode.Plus)
            }
        }
        drawContent()
    }
}
