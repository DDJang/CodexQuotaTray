package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.LiquidIconButton
import com.codexquotatray.android.R
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.ThemePalette
import com.codexquotatray.android.color
import com.kyant.backdrop.backdrops.LayerBackdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop

class LiquidIconButtonFixtureActivity : ComponentActivity() {
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
                LiquidIconButtonFixtureScreen(palette)
            }
        }
    }
}

@Composable
private fun LiquidIconButtonFixtureScreen(palette: ThemePalette) {
    val comparisonBackdrop = rememberLayerBackdrop()
    val currentTopologyBackdrop = rememberLayerBackdrop()
    val sourceBackgroundTopologyBackdrop = rememberLayerBackdrop()
    val richBackdrop = rememberLayerBackdrop()
    val contentColor = palette.color(palette.body)

    Box(
        Modifier
            .fillMaxSize()
            .background(Color.Black),
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 18.dp, vertical = 18.dp),
            verticalArrangement = Arrangement.spacedBy(22.dp),
        ) {
            Text(
                "Liquid Icon Button Fixture",
                color = Color.White,
                style = MaterialTheme.typography.headlineSmall,
            )
            Text(
                "先比较 upstream / production，再隔离 Settings pageBackdrop topology。",
                color = Color.White.copy(alpha = 0.72f),
                style = MaterialTheme.typography.bodyMedium,
            )
            ButtonComparison(backdrop = comparisonBackdrop, contentColor = contentColor)
            SettingsTopologyCase(
                title = "Case A · current Settings topology",
                subtitle = "black background 在 backdrop source 外",
                backdrop = currentTopologyBackdrop,
                sourceIncludesBackground = false,
            )
            SettingsTopologyCase(
                title = "Case B · background enters source",
                subtitle = "唯一差异：layerBackdrop 后同一 source 内 background",
                backdrop = sourceBackgroundTopologyBackdrop,
                sourceIncludesBackground = true,
            )
            SettingsTopologyCase(
                title = "Case C · rich backdrop control",
                subtitle = "gradient / colored shapes / text，确认正常不透明 source 下的表现",
                backdrop = richBackdrop,
                sourceIncludesBackground = true,
                richBackdrop = true,
            )
            Text(
                "A/B/C only use static local fixture content; no quota, OAuth, LAN, worker, or network access.",
                color = Color.White.copy(alpha = 0.65f),
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

@Composable
private fun ButtonComparison(
    backdrop: LayerBackdrop,
    contentColor: Color,
) {
    var upstreamClicks by remember { mutableIntStateOf(0) }
    var productionClicks by remember { mutableIntStateOf(0) }

    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "A/B · same 52dp square, same icon, same backdrop",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Box(
            Modifier
                .fillMaxWidth()
                .height(190.dp),
        ) {
            Box(
                Modifier
                    .fillMaxSize()
                    .layerBackdrop(backdrop),
            ) {
                ComparisonBackdrop()
            }
            Row(
                Modifier
                    .align(Alignment.Center)
                    .padding(horizontal = 10.dp),
                horizontalArrangement = Arrangement.spacedBy(28.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                ComparisonButton(
                    label = "A · upstream",
                    count = upstreamClicks,
                    contentColor = contentColor,
                ) {
                    UpstreamLiquidIconButton(
                        iconRes = R.drawable.ic_settings,
                        backdrop = backdrop,
                        buttonSize = 52.dp,
                        iconSize = 24.dp,
                        onClick = { upstreamClicks++ },
                    )
                }
                ComparisonButton(
                    label = "B · production",
                    count = productionClicks,
                    contentColor = contentColor,
                ) {
                    LiquidIconButton(
                        iconRes = R.drawable.ic_settings,
                        description = "Production fixture button",
                        backdrop = backdrop,
                        buttonSize = 52.dp,
                        iconSize = 24.dp,
                        onClick = { productionClicks++ },
                    )
                }
            }
        }
        Text(
            "按下、拖动、松开后观察玻璃、press scale、translation 和 drag scale；点击计数仅用于确认入口。A=$upstreamClicks  B=$productionClicks",
            color = Color.White.copy(alpha = 0.7f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun ComparisonButton(
    label: String,
    count: Int,
    contentColor: Color,
    button: @Composable () -> Unit,
) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        button()
        Text(label, color = contentColor, fontSize = 11.sp)
        Text("tap $count", color = Color.White.copy(alpha = 0.7f), fontSize = 10.sp)
    }
}

@Composable
private fun SettingsTopologyCase(
    title: String,
    subtitle: String,
    backdrop: LayerBackdrop,
    sourceIncludesBackground: Boolean,
    richBackdrop: Boolean = false,
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(title, color = Color.White, style = MaterialTheme.typography.titleMedium)
        Text(subtitle, color = Color.White.copy(alpha = 0.72f), style = MaterialTheme.typography.bodySmall)
        Box(
            Modifier
                .fillMaxWidth()
                .height(320.dp),
        ) {
            when {
                richBackdrop -> {
                    Box(
                        Modifier
                            .fillMaxSize()
                            .background(Color.Black),
                    ) {
                        Box(
                            Modifier
                                .fillMaxSize()
                                .layerBackdrop(backdrop),
                        ) {
                            RichBackdrop()
                        }
                    }
                }
                sourceIncludesBackground -> {
                    Box(
                        Modifier
                            .fillMaxSize()
                            .layerBackdrop(backdrop)
                            .background(Color.Black),
                    ) {
                        SettingsLikeScrollContent()
                    }
                }
                else -> {
                    Box(
                        Modifier
                            .fillMaxSize()
                            .background(Color.Black),
                    ) {
                        Box(
                            Modifier
                                .fillMaxSize()
                                .layerBackdrop(backdrop),
                        ) {
                            SettingsLikeScrollContent()
                        }
                    }
                }
            }
            LiquidIconButton(
                iconRes = R.drawable.ic_back,
                description = "Fixture 返回按钮",
                backdrop = backdrop,
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(start = 18.dp, top = 12.dp),
                buttonSize = 52.dp,
                iconSize = 24.dp,
                onClick = {},
            )
        }
        Text(
            "在上方固定按钮下面拖动内部内容，让小标题、正文、Card、Divider 依次经过按钮。",
            color = Color.White.copy(alpha = 0.65f),
            style = MaterialTheme.typography.bodySmall,
        )
    }
}

@Composable
private fun SettingsLikeScrollContent() {
    Column(
        Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Spacer(Modifier.height(18.dp))
        Text(
            "账号与配对",
            color = Color.White,
            style = MaterialTheme.typography.titleMedium,
        )
        Text(
            "这是经过返回按钮下方的普通较大正文，用来观察 refraction 和 blur 的连续变化。",
            color = Color.White.copy(alpha = 0.9f),
            style = MaterialTheme.typography.bodyLarge,
        )
        Card(
            Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xFF273142)),
        ) {
            Column(
                Modifier.padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text("已配对设备", color = Color.White, style = MaterialTheme.typography.titleSmall)
                Text(
                    "实色 Card：Windows fallback / LAN pairing fixture",
                    color = Color.White.copy(alpha = 0.78f),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
        }
        HorizontalDivider(color = Color.White.copy(alpha = 0.38f))
        Text(
            "继续滚动：Divider 和多段正文也会从按钮下方经过。",
            color = Color.White.copy(alpha = 0.88f),
            style = MaterialTheme.typography.bodyLarge,
        )
        repeat(4) { index ->
            Text(
                "第 ${index + 1} 段普通大正文：page content remains static and local while the scroll position changes behind the fixed LiquidIconButton.",
                color = Color.White.copy(alpha = 0.82f),
                style = MaterialTheme.typography.bodyLarge,
            )
        }
        Spacer(Modifier.height(120.dp))
    }
}

@Composable
private fun ComparisonBackdrop() {
    Box(
        Modifier
            .fillMaxSize()
            .background(
                Brush.linearGradient(
                    listOf(
                        Color(0xFF0A2740),
                        Color(0xFF4D1459),
                        Color(0xFF075D67),
                    ),
                ),
            ),
    ) {
        Box(
            Modifier
                .offset(x = (-52).dp, y = 34.dp)
                .size(190.dp)
                .background(Color(0xFF2A86FF).copy(alpha = 0.78f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.TopEnd)
                .offset(x = 54.dp, y = 58.dp)
                .size(210.dp)
                .background(Color(0xFFE047FF).copy(alpha = 0.62f), CircleShape),
        )
        Text(
            "STATIC SHARED BACKDROP",
            Modifier
                .align(Alignment.Center)
                .background(Color.Black.copy(alpha = 0.2f), RoundedCornerShape(14.dp))
                .padding(horizontal = 16.dp, vertical = 10.dp),
            color = Color.White.copy(alpha = 0.84f),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}

@Composable
private fun RichBackdrop() {
    Box(
        Modifier
            .fillMaxSize()
            .background(
                Brush.linearGradient(
                    listOf(
                        Color(0xFF081D3A),
                        Color(0xFF281044),
                        Color(0xFF064E54),
                    ),
                ),
            ),
    ) {
        Box(
            Modifier
                .offset(x = (-50).dp, y = 80.dp)
                .size(220.dp)
                .background(Color(0xFF256BFF).copy(alpha = 0.76f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.TopEnd)
                .offset(x = 60.dp, y = 128.dp)
                .size(240.dp)
                .background(Color(0xFFE034FF).copy(alpha = 0.58f), CircleShape),
        )
        Box(
            Modifier
                .align(Alignment.BottomCenter)
                .offset(y = 120.dp)
                .size(280.dp)
                .background(Color(0xFF00D6C2).copy(alpha = 0.52f), CircleShape),
        )
        Text(
            "RICH COLORED SOURCE\nTEXT + SHAPES",
            Modifier
                .align(Alignment.Center)
                .background(Color.Black.copy(alpha = 0.18f), RoundedCornerShape(16.dp))
                .padding(horizontal = 18.dp, vertical = 12.dp),
            color = Color.White.copy(alpha = 0.86f),
            style = MaterialTheme.typography.labelLarge,
        )
    }
}
