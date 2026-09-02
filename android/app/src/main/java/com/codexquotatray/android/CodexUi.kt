package com.codexquotatray.android

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.layout.widthIn
import androidx.compose.ui.semantics.paneTitle
import androidx.compose.ui.semantics.semantics
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

internal object CodexColors {
    val backgroundDark = Color.Black
    val surfaceDark = Color(0xFF252525)
    val surfaceSecondaryDark = Color(0xFF303030)
    val textPrimaryDark = Color(0xFFF5F5F5)
    val textSecondaryDark = Color(0xFF969696)
    val accentDark = Color(0xFF0091FF)
    val accentLight = Color(0xFF0088FF)
    val danger = Color(0xFFFF5A5F)
    val dividerDark = Color(0xFF343434)
}

internal object CodexDimensions {
    val screenPadding = 20.dp
    val cardRadius = 24.dp
    val buttonRadius = 18.dp
    val rowHeight = 52.dp
    val headerHeight = 68.dp
}

internal object CodexTypography {
    val title = TextStyle(fontSize = 21.sp, fontWeight = FontWeight.Bold)
    val body = TextStyle(fontSize = 15.sp)
    val caption = TextStyle(fontSize = 12.sp)
}

internal enum class CodexButtonStyle { PRIMARY, SECONDARY, DANGER }

@Composable
internal fun rememberSystemHapticClick(onClick: () -> Unit): () -> Unit {
    val hapticFeedback = LocalHapticFeedback.current
    val currentOnClick = rememberUpdatedState(onClick)
    return remember(hapticFeedback) {
        {
            hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
            currentOnClick.value()
        }
    }
}

@Composable
internal fun rememberSystemHapticChange(onChange: (Boolean) -> Unit): (Boolean) -> Unit {
    val hapticFeedback = LocalHapticFeedback.current
    val currentOnChange = rememberUpdatedState(onChange)
    return remember(hapticFeedback) {
        { value ->
            hapticFeedback.performHapticFeedback(HapticFeedbackType.ContextClick)
            currentOnChange.value(value)
        }
    }
}

@Composable
internal fun CodexButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    style: CodexButtonStyle = CodexButtonStyle.SECONDARY,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnClick = rememberSystemHapticClick(onClick)
    val dark = palette.color(palette.background).luminance() < 0.1f
    val container = when (style) {
        CodexButtonStyle.PRIMARY -> palette.color(palette.accent)
        CodexButtonStyle.SECONDARY -> if (dark) CodexColors.surfaceSecondaryDark else palette.color(palette.secondaryButton)
        CodexButtonStyle.DANGER -> if (dark) CodexColors.surfaceSecondaryDark else palette.color(palette.secondaryButton)
    }
    val content = when (style) {
        CodexButtonStyle.PRIMARY -> Color.White
        CodexButtonStyle.SECONDARY -> if (dark) CodexColors.textPrimaryDark else palette.color(palette.secondaryButtonText)
        CodexButtonStyle.DANGER -> CodexColors.danger
    }
    Button(
        onClick = hapticOnClick,
        modifier = modifier.height(CodexDimensions.rowHeight),
        enabled = enabled,
        shape = RoundedCornerShape(CodexDimensions.buttonRadius),
        colors = ButtonDefaults.buttonColors(
            containerColor = container,
            contentColor = content,
            disabledContainerColor = container.copy(alpha = 0.45f),
            disabledContentColor = content.copy(alpha = 0.55f),
        ),
    ) {
        Text(text, fontSize = 14.sp, fontWeight = FontWeight.Bold)
    }
}

@Composable
internal fun CodexCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val dark = palette.color(palette.background).luminance() < 0.1f
    Card(
        modifier = modifier,
        shape = RoundedCornerShape(CodexDimensions.cardRadius),
        colors = CardDefaults.cardColors(
            containerColor = if (dark) CodexColors.surfaceDark else palette.color(palette.surface),
        ),
    ) {
        Column(Modifier.fillMaxWidth().padding(18.dp), content = content)
    }
}

@Composable
internal fun CodexConfirmDialog(
    backdrop: Backdrop,
    title: String,
    message: String,
    confirmText: String,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticConfirm = rememberSystemHapticClick { onConfirm(); onDismiss() }
    val hapticDismiss = rememberSystemHapticClick(onDismiss)
    LiquidModalOverlay(
        backdrop = backdrop,
        paneTitle = title,
        onDismiss = onDismiss,
    ) {
        LiquidDialogSurface(
            backdrop = backdrop,
            modifier = Modifier
                .widthIn(min = 280.dp, max = 420.dp)
                .semantics { paneTitle = title },
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 20.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(
                    text = title,
                    style = CodexTypography.title,
                    color = palette.color(palette.title),
                )
                Text(
                    text = message,
                    style = CodexTypography.body,
                    color = palette.color(palette.body),
                )
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.End,
                ) {
                    TextButton(onClick = hapticDismiss) {
                        Text("取消", color = palette.color(palette.secondary))
                    }
                    TextButton(onClick = hapticConfirm) {
                        Text(confirmText, color = CodexColors.danger)
                    }
                }
            }
        }
    }
}

@Composable
internal fun SecondaryScreenScaffold(
    title: String,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
    modalContent: @Composable BoxScope.(Backdrop) -> Unit = {},
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val backdrop = rememberLayerBackdrop()
    val scrollState = rememberScrollState()
    var upwardOverscrollActive by remember { mutableStateOf(false) }
    val backgroundColor = palette.color(palette.background)
    Box(modifier.fillMaxSize()) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
                .background(backgroundColor),
        ) {
            Column(
                Modifier
                    .fillMaxSize()
                    .dampedVerticalOverscroll { displacement ->
                        upwardOverscrollActive = displacement < 0f
                    }
                    .verticalScroll(scrollState, overscrollEffect = null)
                    .statusBarsPadding()
                    .navigationBarsPadding()
                    .padding(start = 20.dp, end = 20.dp, top = 86.dp, bottom = 32.dp),
                verticalArrangement = Arrangement.spacedBy(28.dp),
            ) {
                content()
            }
        }
        SettingsGradientBlurHeader(
            backdrop = backdrop,
            scrollState = scrollState,
            isScrolled = scrollState.value > 0 || upwardOverscrollActive,
            tint = backgroundColor,
            modifier = Modifier.align(Alignment.TopCenter),
        )
        Row(
            Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 18.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            LiquidIconButton(
                iconRes = R.drawable.ic_back,
                description = "返回",
                backdrop = backdrop,
                buttonSize = 48.dp,
                iconSize = 25.dp,
                onClick = onBack,
            )
            Text(
                title,
                Modifier.weight(1f),
                style = CodexTypography.title,
                color = palette.color(palette.title),
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.size(48.dp))
        }
        modalContent(backdrop)
    }
}
