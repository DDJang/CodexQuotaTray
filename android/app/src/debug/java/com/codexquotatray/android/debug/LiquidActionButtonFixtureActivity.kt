package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.LocalIndication
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.isSpecified
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.util.fastCoerceAtMost
import androidx.compose.ui.util.fastCoerceIn
import androidx.compose.ui.util.lerp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexColors
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SettingsActionButton
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.ThemePalette
import com.codexquotatray.android.color
import com.codexquotatray.android.rememberSystemHapticClick
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.shapes.Capsule
import kotlinx.coroutines.delay
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.tanh

class LiquidActionButtonFixtureActivity : ComponentActivity() {
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
                LiquidActionButtonFixtureScreen(palette)
            }
        }
    }
}

@Composable
private fun LiquidActionButtonFixtureScreen(palette: ThemePalette) {
    val backdrop = rememberLayerBackdrop()
    var clickCount by remember { mutableIntStateOf(0) }
    var stateMutationChecking by remember { mutableStateOf(false) }

    LaunchedEffect(stateMutationChecking) {
        if (stateMutationChecking) {
            delay(1500)
            stateMutationChecking = false
        }
    }

    Box(Modifier.fillMaxSize()) {
        Box(
            Modifier
                .fillMaxSize()
                .layerBackdrop(backdrop)
                .background(palette.color(palette.background)),
        ) {
            FixtureBackdrop(palette)
        }

        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 18.dp, vertical = 20.dp),
            verticalArrangement = Arrangement.spacedBy(24.dp),
        ) {
            Text(
                "Liquid Action Button Fixture",
                color = Color.White,
                style = MaterialTheme.typography.headlineSmall,
            )
            Text(
                "Current Material / exact Kyant upstream / bounded production candidate",
                color = Color.White.copy(alpha = 0.72f),
                style = MaterialTheme.typography.bodyMedium,
            )

            CurrentMaterialSection(palette = palette, onClick = { clickCount++ })
            ExactUpstreamSection(
                backdrop = backdrop,
                palette = palette,
                onClick = { clickCount++ },
            )
            SettingsLikeSection(
                backdrop = backdrop,
                palette = palette,
                onClick = { clickCount++ },
            )
            ProductionCandidateSection(
                backdrop = backdrop,
                palette = palette,
                onClick = { clickCount++ },
            )
            StateMutationRegressionSection(
                checking = stateMutationChecking,
                onClick = {
                    if (!stateMutationChecking) {
                        clickCount++
                        stateMutationChecking = true
                    }
                },
            )

            Text(
                "click count: $clickCount",
                color = Color.White,
                style = MaterialTheme.typography.titleMedium,
            )
            Text(
                "All content is local debug state. No quota, token, OAuth, Windows, worker, updater, or network access.",
                color = Color.White.copy(alpha = 0.68f),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun CurrentMaterialSection(
    palette: ThemePalette,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            "1. Current Material",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            "Current Material action visual; callbacks only increment this fixture counter.",
            color = Color.White.copy(alpha = 0.72f),
            style = MaterialTheme.typography.bodySmall,
        )
        CurrentMaterialButton(
            label = "重新登录",
            palette = palette,
            onClick = onClick,
        )
        CurrentMaterialButton(
            label = "下载并安装",
            palette = palette,
            primary = true,
            onClick = onClick,
        )
        CurrentMaterialButton(
            label = "退出登录",
            palette = palette,
            danger = true,
            onClick = onClick,
        )
        CurrentMaterialButton(
            label = "重新登录（disabled）",
            palette = palette,
            enabled = false,
            onClick = onClick,
        )
    }
}

@Composable
private fun CurrentMaterialButton(
    label: String,
    palette: ThemePalette,
    enabled: Boolean = true,
    primary: Boolean = false,
    danger: Boolean = false,
    onClick: () -> Unit,
) {
    val container = if (primary) {
        palette.color(palette.primaryButton)
    } else {
        palette.color(palette.secondaryButton)
    }
    val content = when {
        danger -> CodexColors.danger
        primary -> palette.color(palette.onPrimary)
        else -> palette.color(palette.secondaryButtonText)
    }
    Button(
        onClick = rememberSystemHapticClick(onClick),
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 4.dp)
            .height(52.dp),
        enabled = enabled,
        shape = RoundedCornerShape(18.dp),
        contentPadding = PaddingValues(horizontal = 16.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = container,
            contentColor = content,
            disabledContainerColor = container.copy(alpha = 0.45f),
            disabledContentColor = content.copy(alpha = 0.55f),
        ),
    ) {
        Text(text = label, fontSize = 15.sp, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun ExactUpstreamSection(
    backdrop: Backdrop,
    palette: ThemePalette,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "2. Exact Kyant upstream",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            "48dp height · 16dp horizontal padding · 8dp content spacing · no project optical changes",
            color = Color.White.copy(alpha = 0.72f),
            style = MaterialTheme.typography.bodySmall,
        )
        NaturalWidthAction("Neutral") {
            UpstreamLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
            ) {
                FixtureActionText("重新登录", palette.color(palette.body))
            }
        }
        NaturalWidthAction("Accent tint · palette accent") {
            UpstreamLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
                tint = palette.color(palette.accent),
            ) {
                FixtureActionText("下载并安装", Color.White)
            }
        }
        NaturalWidthAction("Danger tint · CodexColors.danger") {
            UpstreamLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
                tint = CodexColors.danger,
            ) {
                FixtureActionText("退出登录", Color.White)
            }
        }
        NaturalWidthAction("isInteractive=false · upstream visual reference") {
            UpstreamLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
                isInteractive = false,
            ) {
                FixtureActionText("重新登录", palette.color(palette.body))
            }
        }
    }
}

@Composable
private fun SettingsLikeSection(
    backdrop: Backdrop,
    palette: ThemePalette,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "3. Settings-like full width",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            "Production-like Card and 12dp horizontal inset; LiquidButton remains upstream 48dp high.",
            color = Color.White.copy(alpha = 0.72f),
            style = MaterialTheme.typography.bodySmall,
        )
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(24.dp),
            colors = CardDefaults.cardColors(
                containerColor = palette.color(palette.surface).copy(alpha = 0.88f),
            ),
        ) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .padding(vertical = 2.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                SettingsLikeAction(
                    label = "Neutral",
                    button = {
                        UpstreamLiquidButton(
                            onClick = rememberSystemHapticClick(onClick),
                            backdrop = backdrop,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 12.dp),
                        ) {
                            FixtureActionText("重新登录", palette.color(palette.body))
                        }
                    },
                )
                HorizontalDivider(color = palette.color(palette.border).copy(alpha = 0.7f))
                SettingsLikeAction(
                    label = "Accent tint",
                    button = {
                        UpstreamLiquidButton(
                            onClick = rememberSystemHapticClick(onClick),
                            backdrop = backdrop,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 12.dp),
                            tint = palette.color(palette.accent),
                        ) {
                            FixtureActionText("下载并安装", Color.White)
                        }
                    },
                )
                HorizontalDivider(color = palette.color(palette.border).copy(alpha = 0.7f))
                SettingsLikeAction(
                    label = "Danger tint",
                    button = {
                        UpstreamLiquidButton(
                            onClick = rememberSystemHapticClick(onClick),
                            backdrop = backdrop,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 12.dp),
                            tint = CodexColors.danger,
                        ) {
                            FixtureActionText("退出登录", Color.White)
                        }
                    },
                )
            }
        }
    }
}

@Composable
private fun ProductionCandidateSection(
    backdrop: Backdrop,
    palette: ThemePalette,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "4. Production candidate · bounded drag",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            "唯一差异：rawOffset 仅在 visual deformation 输入处 clamp 到 size.minDimension；真实拖动仍继续。",
            color = Color.White.copy(alpha = 0.72f),
            style = MaterialTheme.typography.bodySmall,
        )
        NaturalWidthAction("Neutral · bounded drag") {
            BoundedLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
            ) {
                FixtureActionText("重新登录", palette.color(palette.body))
            }
        }
        NaturalWidthAction("Accent tint · bounded drag") {
            BoundedLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
                tint = palette.color(palette.accent),
            ) {
                FixtureActionText("下载并安装", Color.White)
            }
        }
        NaturalWidthAction("Danger tint · bounded drag") {
            BoundedLiquidButton(
                onClick = rememberSystemHapticClick(onClick),
                backdrop = backdrop,
                tint = CodexColors.danger,
            ) {
                FixtureActionText("退出登录", Color.White)
            }
        }
    }
}

@Composable
private fun StateMutationRegressionSection(
    checking: Boolean,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "5. State mutation on click regression",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            "点击回调立即切换文案和 enabled；1.5 秒后自动恢复，用于观察 release spring 是否完整播放。",
            color = Color.White.copy(alpha = 0.72f),
            style = MaterialTheme.typography.bodySmall,
        )
        SettingsActionButton(
            label = if (checking) "正在检查…" else "检查更新",
            enabled = !checking,
            onClick = onClick,
        )
        Text(
            if (checking) {
                "当前已 disabled：不可重复点击或开始新的 liquid gesture。"
            } else {
                "点击后立即 disabled，观察按压放大和松手回弹。"
            },
            color = Color.White.copy(alpha = 0.62f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun NaturalWidthAction(
    label: String,
    button: @Composable () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(
            label,
            color = Color.White.copy(alpha = 0.78f),
            style = MaterialTheme.typography.labelLarge,
        )
        Box(
            Modifier.fillMaxWidth(),
            contentAlignment = Alignment.Center,
        ) {
            button()
        }
    }
}

@Composable
private fun SettingsLikeAction(
    label: String,
    button: @Composable () -> Unit,
) {
    Column(
        Modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Text(
            label,
            modifier = Modifier.padding(horizontal = 20.dp),
            color = Color.White.copy(alpha = 0.62f),
            fontSize = 11.sp,
        )
        button()
    }
}

@Composable
private fun FixtureActionText(text: String, color: Color) {
    Text(
        text = text,
        color = color,
        fontSize = 15.sp,
        fontWeight = FontWeight.Medium,
    )
}

@Composable
private fun BoundedLiquidButton(
    onClick: () -> Unit,
    backdrop: Backdrop,
    modifier: Modifier = Modifier,
    isInteractive: Boolean = true,
    tint: Color = Color.Unspecified,
    surfaceColor: Color = Color.Unspecified,
    content: @Composable RowScope.() -> Unit,
) {
    val animationScope = rememberCoroutineScope()
    val interactiveHighlight = remember(animationScope) {
        com.codexquotatray.android.liquidglass.InteractiveHighlight(
            animationScope = animationScope,
        )
    }
    Row(
        modifier
            .drawBackdrop(
                backdrop = backdrop,
                shape = { Capsule() },
                effects = {
                    vibrancy()
                    blur(2f.dp.toPx())
                    lens(12f.dp.toPx(), 24f.dp.toPx())
                },
                layerBlock = if (isInteractive) {
                    {
                        val width = size.width
                        val height = size.height
                        val progress = interactiveHighlight.pressProgress
                        val scale = lerp(1f, 1f + 4f.dp.toPx() / size.height, progress)
                        val maxOffset = size.minDimension
                        val initialDerivative = 0.05f
                        val rawOffset = interactiveHighlight.offset
                        val offset = Offset(
                            rawOffset.x.fastCoerceIn(-maxOffset, maxOffset),
                            rawOffset.y.fastCoerceIn(-maxOffset, maxOffset),
                        )
                        translationX = maxOffset * tanh(initialDerivative * offset.x / maxOffset)
                        translationY = maxOffset * tanh(initialDerivative * offset.y / maxOffset)
                        val maxDragScale = 4f.dp.toPx() / size.height
                        val offsetAngle = atan2(offset.y, offset.x)
                        scaleX = scale +
                            maxDragScale * abs(cos(offsetAngle) * offset.x / size.maxDimension) *
                            (width / height).fastCoerceAtMost(1f)
                        scaleY = scale +
                            maxDragScale * abs(sin(offsetAngle) * offset.y / size.maxDimension) *
                            (height / width).fastCoerceAtMost(1f)
                    }
                } else {
                    null
                },
                onDrawSurface = {
                    if (tint.isSpecified) {
                        drawRect(tint, blendMode = BlendMode.Hue)
                        drawRect(tint.copy(alpha = 0.75f))
                    }
                    if (surfaceColor.isSpecified) {
                        drawRect(surfaceColor)
                    }
                },
            )
            .clickable(
                interactionSource = null,
                indication = if (isInteractive) null else LocalIndication.current,
                role = Role.Button,
                onClick = onClick,
            )
            .then(
                if (isInteractive) {
                    Modifier
                        .then(interactiveHighlight.modifier)
                        .then(interactiveHighlight.gestureModifier)
                } else {
                    Modifier
                },
            )
            .height(48f.dp)
            .padding(horizontal = 16f.dp),
        horizontalArrangement = Arrangement.spacedBy(8f.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
        content = content,
    )
}

@Composable
private fun FixtureBackdrop(palette: ThemePalette) {
    Box(
        Modifier
            .fillMaxSize()
            .background(
                Brush.linearGradient(
                    listOf(
                        Color(0xFF071A35),
                        Color(0xFF241044),
                        Color(0xFF063E4B),
                    ),
                ),
            ),
    ) {
        Box(
            Modifier
                .offset(x = (-62).dp, y = 140.dp)
                .size(260.dp)
                .background(Color(0xFF236BFF).copy(alpha = 0.72f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.TopEnd)
                .offset(x = 74.dp, y = 220.dp)
                .size(280.dp)
                .background(Color(0xFFDB38FF).copy(alpha = 0.6f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.BottomCenter)
                .offset(y = 180.dp)
                .size(320.dp)
                .background(Color(0xFF00D8C4).copy(alpha = 0.48f), CircleShape),
        )
        Card(
            Modifier
                .align(Alignment.Center)
                .padding(horizontal = 24.dp)
                .fillMaxWidth()
                .height(220.dp),
            shape = RoundedCornerShape(24.dp),
            colors = CardDefaults.cardColors(
                containerColor = palette.color(palette.surface).copy(alpha = 0.72f),
            ),
        ) {
            Column(
                Modifier.padding(20.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(
                    "SETTINGS-LIKE SOURCE",
                    color = Color.White.copy(alpha = 0.68f),
                    style = MaterialTheme.typography.labelLarge,
                )
                Text(
                    "Rich backdrop content",
                    color = Color.White,
                    style = MaterialTheme.typography.titleLarge,
                )
                Text(
                    "Blue / purple / cyan blocks, text and a dark Card make blur, lens, refraction and tint visible.",
                    color = Color.White.copy(alpha = 0.76f),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
        }
    }
}
