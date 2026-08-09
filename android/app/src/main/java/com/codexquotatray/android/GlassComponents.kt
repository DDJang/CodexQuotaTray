package com.codexquotatray.android

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.EaseOut
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.foundation.MutatorMutex
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
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
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.input.pointer.AwaitPointerEventScope
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.PointerId
import androidx.compose.ui.input.pointer.PointerInputChange
import androidx.compose.ui.input.pointer.PointerInputScope
import androidx.compose.ui.input.pointer.changedToUpIgnoreConsumed
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.layout.LayoutCoordinates
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
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
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.sign
import kotlin.time.Clock

@Composable
internal fun GlassIconButton(
    text: String,
    description: String,
    backdrop: Backdrop,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val pressProgress by animateFloatAsState(
        if (pressed) 1f else 0f,
        spring(dampingRatio = 0.62f, stiffness = 520f),
        label = "glass-button-press",
    )
    Box(
        Modifier
            .size(48.dp)
            .drawBackdrop(
                backdrop = backdrop,
                shape = { KyantShapes.capsule() },
                effects = {
                    vibrancy()
                    blur(8.dp.toPx())
                    lens(18.dp.toPx() * (0.7f + 0.3f * pressProgress), 18.dp.toPx(), chromaticAberration = true)
                },
                highlight = { Highlight.Default },
                shadow = { Shadow(alpha = 0.7f) },
                innerShadow = { InnerShadow(radius = 5.dp, alpha = 0.55f) },
                layerBlock = {
                    val scale = lerp(1f, 0.9f, pressProgress)
                    scaleX = scale
                    scaleY = scale
                },
                onDrawSurface = { drawRect(palette.color(palette.surface).copy(alpha = 0.36f)) },
            )
            .clip(KyantShapes.capsule())
            .clickable(
                interactionSource = interaction,
                indication = null,
                enabled = enabled,
                role = Role.Button,
                onClick = onClick,
            )
            .semantics { contentDescription = description }
            .alpha(if (enabled) 1f else 0.45f),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, color = palette.color(palette.title), fontSize = 21.sp)
    }
}

private val LocalDockTabScale = compositionLocalOf { { 1f } }

@Composable
private fun RowScope.DockTab(onClick: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    val scale = LocalDockTabScale.current
    Column(
        Modifier
            .clip(KyantShapes.capsule())
            .clickable(interactionSource = null, indication = null, role = Role.Tab, onClick = onClick)
            .fillMaxHeight()
            .weight(1f)
            .graphicsLayer { scaleX = scale(); scaleY = scale() },
        verticalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
        content = content,
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
    val palette = LocalQuotaPalette.current
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
        LiquidTabCapsule(selectedIndex, onSelected, backdrop, Modifier.weight(1f))
        GlassIconButton(if (actionBusy) "…" else "↻", "刷新当前页面", backdrop, actionEnabled && !actionBusy, onAction)
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
    val tabsBackdrop = rememberLayerBackdrop()
    val scope = rememberCoroutineScope()

    BoxWithConstraints(modifier, contentAlignment = Alignment.CenterStart) {
        val density = LocalDensity.current
        val tabWidth = with(density) { (constraints.maxWidth.toFloat() - 8.dp.toPx()) / 2f }
        val offsetAnimation = remember { Animatable(0f) }
        val panelOffset by remember(density) {
            derivedStateOf {
                val fraction = (offsetAnimation.value / constraints.maxWidth).fastCoerceIn(-1f, 1f)
                with(density) { 4.dp.toPx() * fraction.sign * EaseOut.transform(abs(fraction)) }
            }
        }
        val isLtr = LocalLayoutDirection.current == LayoutDirection.Ltr
        var currentIndex by remember { mutableIntStateOf(selectedIndex) }
        val drag = remember(scope) {
            DampedDockDrag(
                scope, selectedIndex.toFloat(), 0f..1f,
                onStopped = {
                    val projected = targetValue + (velocity / 10f).fastCoerceIn(-0.35f, 0.35f)
                    val target = projected.fastRoundToInt().fastCoerceIn(0, 1)
                    currentIndex = target
                    animateToValue(target.toFloat())
                    scope.launch { offsetAnimation.animateTo(0f, spring(1f, 300f, 0.5f)) }
                },
                onDrag = { _, amount ->
                    updateValue((targetValue + amount.x / tabWidth * if (isLtr) 1f else -1f).fastCoerceIn(0f, 1f))
                    scope.launch { offsetAnimation.snapTo(offsetAnimation.value + amount.x) }
                },
            )
        }
        LaunchedEffect(selectedIndex) {
            currentIndex = selectedIndex
            drag.animateToValue(selectedIndex.toFloat())
        }
        LaunchedEffect(drag) {
            snapshotFlow { currentIndex }.drop(1).collectLatest { onSelected(it) }
        }

        val tabs: @Composable RowScope.() -> Unit = {
            DockTab({ currentIndex = 0 }) { Text("额度", fontSize = 13.sp) }
            DockTab({ currentIndex = 1 }) { Text("统计", fontSize = 13.sp) }
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
                .height(64.dp).fillMaxWidth().padding(4.dp),
            verticalAlignment = Alignment.CenterVertically,
            content = tabs,
        )
        CompositionLocalProvider(LocalDockTabScale provides { lerp(1f, 1.2f, drag.pressProgress) }) {
            Row(
                Modifier.clearAndSetSemantics {}.alpha(0f).layerBackdrop(tabsBackdrop)
                    .graphicsLayer { translationX = panelOffset }
                    .drawBackdrop(
                        backdrop, { KyantShapes.capsule() },
                        effects = { vibrancy(); blur(8.dp.toPx()); lens(24.dp.toPx() * drag.pressProgress, 24.dp.toPx() * drag.pressProgress) },
                        highlight = { Highlight.Default.copy(alpha = drag.pressProgress) },
                        onDrawSurface = { drawRect(containerColor) },
                    )
                    .height(56.dp).fillMaxWidth().padding(horizontal = 4.dp)
                    .graphicsLayer(colorFilter = ColorFilter.tint(accentColor)),
                verticalAlignment = Alignment.CenterVertically,
                content = tabs,
            )
        }
        Box(
            Modifier.padding(horizontal = 4.dp)
                .graphicsLayer { translationX = if (isLtr) drag.value * tabWidth + panelOffset else size.width - (drag.value + 1f) * tabWidth + panelOffset }
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

private class DampedDockDrag(
    private val scope: CoroutineScope,
    initial: Float,
    private val range: ClosedRange<Float>,
    private val onStopped: DampedDockDrag.() -> Unit,
    private val onDrag: DampedDockDrag.(IntSize, Offset) -> Unit,
) {
    private val valueAnim = Animatable(initial, 0.001f)
    private val velocityAnim = Animatable(0f, 5f)
    private val pressAnim = Animatable(0f, 0.001f)
    private val scaleXAnim = Animatable(1f, 0.001f)
    private val scaleYAnim = Animatable(1f, 0.001f)
    private val mutex = MutatorMutex()
    private val tracker = VelocityTracker()
    val value get() = valueAnim.value
    val targetValue get() = valueAnim.targetValue
    val pressProgress get() = pressAnim.value
    val scaleX get() = scaleXAnim.value
    val scaleY get() = scaleYAnim.value
    val velocity get() = velocityAnim.value
    val modifier = Modifier.pointerInput(Unit) {
        inspectDockDrag(
            onStart = { tracker.resetTracking(); press() },
            onEnd = { onStopped(); release() },
            onCancel = { onStopped(); release() },
        ) { _, amount -> onDrag(size, amount) }
    }
    private fun press() { scope.launch { launch { pressAnim.animateTo(1f, spring(1f, 1000f, 0.001f)) }; launch { scaleXAnim.animateTo(78f / 56f, spring(0.6f, 250f, 0.001f)) }; launch { scaleYAnim.animateTo(78f / 56f, spring(0.7f, 250f, 0.001f)) } } }
    private fun release() { scope.launch { androidx.compose.runtime.withFrameNanos {}; if (value != targetValue) snapshotFlow { valueAnim.value }.filter { abs(it - valueAnim.targetValue) < 0.025f }.first(); launch { pressAnim.animateTo(0f, spring(1f, 1000f, 0.001f)) }; launch { scaleXAnim.animateTo(1f, spring(0.6f, 250f, 0.001f)) }; launch { scaleYAnim.animateTo(1f, spring(0.7f, 250f, 0.001f)) } } }
    fun updateValue(value: Float) { scope.launch { valueAnim.animateTo(value.coerceIn(range), spring(1f, 1000f, 0.001f)) { tracker.addPosition(Clock.System.now().toEpochMilliseconds(), Offset(this.value, 0f)); val v = tracker.calculateVelocity().x; scope.launch { velocityAnim.animateTo(v, spring(0.5f, 300f, 0.01f)) } } } }
    fun animateToValue(value: Float) { scope.launch { mutex.mutate { press(); launch { valueAnim.animateTo(value.coerceIn(range), spring(1f, 1000f, 0.001f)) }; if (velocity != 0f) launch { velocityAnim.animateTo(0f, spring(0.5f, 300f, 0.01f)) }; release() } } }
}

private suspend fun PointerInputScope.inspectDockDrag(
    onStart: (PointerInputChange) -> Unit,
    onEnd: (PointerInputChange) -> Unit,
    onCancel: () -> Unit,
    onDrag: (PointerInputChange, Offset) -> Unit,
) = awaitEachGesture {
    val initial = awaitFirstDown(false, PointerEventPass.Initial)
    val down = awaitFirstDown(false)
    onStart(down); onDrag(initial, Offset.Zero)
    val up = dragOrUp(initial.id) { onDrag(it, it.positionChange()) }
    if (up == null) onCancel() else onEnd(up)
}

private suspend inline fun AwaitPointerEventScope.dragOrUp(id: PointerId, onDrag: (PointerInputChange) -> Unit): PointerInputChange? {
    if (currentEvent.changes.fastFirstOrNull { it.id == id }?.pressed != true) return null
    var pointer = id
    while (true) {
        val event = awaitPointerEvent()
        val change = event.changes.fastFirstOrNull { it.id == pointer } ?: return null
        if (change.isConsumed) return null
        if (change.changedToUpIgnoreConsumed()) {
            val other = event.changes.fastFirstOrNull { it.pressed } ?: return change
            pointer = other.id
        } else if (change.previousPosition != change.position) onDrag(change)
    }
}
