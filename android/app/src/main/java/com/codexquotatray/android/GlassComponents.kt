package com.codexquotatray.android

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.BasicText
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.paint
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.GraphicsLayerScope
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.TextStyle
import androidx.annotation.DrawableRes
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.fastCoerceAtMost
import androidx.compose.ui.util.lerp
import com.codexquotatray.android.liquidglass.LiquidBottomTab
import com.codexquotatray.android.liquidglass.LiquidBottomTabs
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.colorControls
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.tanh

// Keep the refresh action equal to the short edge of the bottom navigation capsule.
internal val glassActionButtonSize = 64.dp
internal val glassRefreshIconSize = 28.dp

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
        val actionSize = glassActionButtonSize
        val navigationHeight = glassActionButtonSize
        val minimumGap = 16.dp
        val preferredNavigationWidth = (maxWidth * 0.525f).coerceIn(172.dp, 217.dp)
        val navigationWidth = minOf(preferredNavigationWidth, maxWidth - actionSize - minimumGap)
        val palette = LocalQuotaPalette.current
        val contentColor = palette.color(palette.body)
        val selectedIndexState = rememberUpdatedState(selectedIndex)
        val onSelectedState = rememberUpdatedState(onSelected)
        var requestedIndex by remember { mutableIntStateOf(selectedIndex) }
        LaunchedEffect(selectedIndex) {
            requestedIndex = selectedIndex
        }
        val requestedIndexState = rememberUpdatedState(requestedIndex)
        val selectedIndexProvider = remember { { requestedIndexState.value } }
        val selectionSink = remember { { index: Int -> onSelectedState.value(index) } }
        val hapticFeedback = LocalHapticFeedback.current
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            LiquidBottomTabs(
                selectedTabIndex = selectedIndexProvider,
                onTabSelected = { index ->
                    if (selectedIndexState.value != index) {
                        hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
                        selectionSink(index)
                    }
                },
                backdrop = backdrop,
                tabsCount = 2,
                modifier = Modifier.size(width = navigationWidth, height = navigationHeight),
            ) {
                LiquidBottomTab(onClick = { requestedIndex = 0 }) {
                    DockTabContent(
                        R.drawable.ic_quota_tray,
                        "额度",
                        contentColor,
                        iconWidth = 22.dp,
                        iconHeight = 24.dp,
                    )
                }
                LiquidBottomTab(onClick = { requestedIndex = 1 }) {
                    DockTabContent(R.drawable.ic_usage, "统计", contentColor)
                }
            }
            androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
            LiquidIconButton(
                iconRes = R.drawable.ic_refresh,
                description = actionDescription,
                backdrop = backdrop,
                enabled = actionEnabled && !actionBusy,
                busy = actionBusy,
                buttonSize = actionSize,
                iconSize = glassRefreshIconSize,
                onClick = onAction,
            )
        }
    }
}
