package com.codexquotatray.android.debug

import android.os.Bundle
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.SecondaryScreenScaffold
import com.codexquotatray.android.SettingsGroup
import com.codexquotatray.android.SettingsInfoRow
import com.codexquotatray.android.SettingsSection
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SettingsUiTokens
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.color
import com.codexquotatray.android.settingsPalette
import com.codexquotatray.android.widget.QuotaWidgetProjection
import com.codexquotatray.android.widget.QuotaWidgetRenderer
import com.codexquotatray.android.widget.QuotaWidgetTokenSummary
import com.codexquotatray.android.widget.QuotaWidgetWindow

private const val FIXTURE_TIME_MILLIS = 1_788_312_600_000L // 2026-09-02 09:30, Asia/Shanghai
private const val FIXTURE_TIME_SECONDS = 1_788_312_600L

private data class WidgetPreviewSize(
    val widthDp: Int,
    val heightDp: Int,
    val selectorLabel: String,
)

private val widgetPreviewSizes = listOf(
    WidgetPreviewSize(widthDp = 360, heightDp = 132, selectorLabel = "360×132 推荐"),
    WidgetPreviewSize(widthDp = 300, heightDp = 110, selectorLabel = "300×110 最小"),
)

private val defaultWidgetPreviewSizeIndex = widgetPreviewSizes.indexOfFirst {
    it.widthDp == 360 && it.heightDp == 132
}

private val widgetPreviewSizeOptions = widgetPreviewSizes.mapIndexed { index, size ->
    SettingsSegmentOption(index, size.selectorLabel)
}

private enum class QuotaWidgetFixtureScenario(
    val selectorLabel: String,
    val displayLabel: String,
) {
    EMPTY("空状态", "Empty"),
    SINGLE_QUOTA("单窗口", "Single quota"),
    DUAL_QUOTA("双窗口", "Dual quota"),
    DUAL_NO_TOKEN("无 Token", "Dual quota · no Token"),
    PARTIAL_UNAVAILABLE("部分不可用", "Partial unavailable"),
    LARGE_TOKEN("大 Token", "Large Token numbers"),
    THRESHOLD_COLORS("阈值颜色", "Threshold colors"),
}

private val quotaWidgetFixtureOptions = QuotaWidgetFixtureScenario.entries.mapIndexed { index, scenario ->
    SettingsSegmentOption(index, scenario.selectorLabel)
}

private val thresholdOptions = listOf(
    SettingsSegmentOption(82, "82%"),
    SettingsSegmentOption(35, "35%"),
    SettingsSegmentOption(8, "8%"),
)

private val fixtureFiveHour = QuotaWidgetWindow(
    title = "5 小时",
    remainingPercent = 72,
    resetsAt = FIXTURE_TIME_SECONDS + 2 * 60 * 60L,
    windowDurationMins = 5 * 60L,
)

private val fixtureSevenDay = QuotaWidgetWindow(
    title = "7 天",
    remainingPercent = 41,
    resetsAt = FIXTURE_TIME_SECONDS + 5 * 24 * 60 * 60L,
    windowDurationMins = 7 * 24 * 60L,
)

private val fixtureTokenSummary = QuotaWidgetTokenSummary(
    todayTokens = 84_000L,
    last7DaysTokens = 420_000L,
    last30DaysTokens = 1_250_000L,
    lifetimeTokens = 18_400_000L,
)

private val fixtureLargeTokenSummary = QuotaWidgetTokenSummary(
    todayTokens = 2_400_000L,
    last7DaysTokens = 16_800_000L,
    last30DaysTokens = 58_200_000L,
    lifetimeTokens = 312_400_000L,
)

private fun fixtureProjection(
    primary: QuotaWidgetWindow?,
    secondary: QuotaWidgetWindow? = null,
    tokenSummary: QuotaWidgetTokenSummary? = fixtureTokenSummary,
): QuotaWidgetProjection = QuotaWidgetProjection(
    planType = "Plus",
    updatedAtMillis = FIXTURE_TIME_MILLIS,
    primary = primary,
    secondary = secondary,
    tokenSummary = tokenSummary,
)

private fun quotaWidgetFixtureProjection(
    scenario: QuotaWidgetFixtureScenario,
    thresholdPercent: Int,
): QuotaWidgetProjection? = when (scenario) {
    QuotaWidgetFixtureScenario.EMPTY -> null
    QuotaWidgetFixtureScenario.SINGLE_QUOTA -> fixtureProjection(
        primary = fixtureFiveHour,
    )
    QuotaWidgetFixtureScenario.DUAL_QUOTA -> fixtureProjection(
        primary = fixtureFiveHour,
        secondary = fixtureSevenDay,
    )
    QuotaWidgetFixtureScenario.DUAL_NO_TOKEN -> fixtureProjection(
        primary = fixtureFiveHour,
        secondary = fixtureSevenDay,
        tokenSummary = null,
    )
    QuotaWidgetFixtureScenario.PARTIAL_UNAVAILABLE -> fixtureProjection(
        primary = fixtureFiveHour.copy(remainingPercent = null),
        secondary = fixtureSevenDay.copy(remainingPercent = 18),
    )
    QuotaWidgetFixtureScenario.LARGE_TOKEN -> fixtureProjection(
        primary = fixtureFiveHour,
        tokenSummary = fixtureLargeTokenSummary,
    )
    QuotaWidgetFixtureScenario.THRESHOLD_COLORS -> fixtureProjection(
        primary = fixtureFiveHour.copy(remainingPercent = thresholdPercent),
    )
}

class QuotaWidgetFixtureActivity : ComponentActivity() {
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
        val effectiveTheme = AppTheme.effectiveMode(this, selectedTheme)
        val palette = settingsPalette(AppTheme.palette(this, selectedTheme), effectiveTheme)
        setContent {
            CodexQuotaTheme(palette) {
                QuotaWidgetFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun QuotaWidgetFixtureScreen(onBack: () -> Unit) {
    var selectedScenario by remember { mutableIntStateOf(QuotaWidgetFixtureScenario.EMPTY.ordinal) }
    var selectedSize by remember { mutableIntStateOf(defaultWidgetPreviewSizeIndex) }
    var thresholdPercent by remember { mutableIntStateOf(82) }
    val scenario = QuotaWidgetFixtureScenario.entries[selectedScenario]
    val previewSize = widgetPreviewSizes[selectedSize]
    val projection = quotaWidgetFixtureProjection(scenario, thresholdPercent)
    val palette = com.codexquotatray.android.LocalQuotaPalette.current

    SecondaryScreenScaffold(title = "主屏小组件", onBack = onBack) {
        Column(Modifier.fillMaxWidth()) {
            SettingsSection("Debug 场景") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = quotaWidgetFixtureOptions,
                        selectedValue = selectedScenario,
                        enabled = true,
                        onSelected = { selectedScenario = it },
                    )
                    SettingsInfoRow("当前场景", scenario.displayLabel, valueMaxLines = 2)
                }
            }
            SettingsSection("尺寸") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = widgetPreviewSizeOptions,
                        selectedValue = selectedSize,
                        enabled = true,
                        onSelected = { selectedSize = it },
                    )
                    SettingsInfoRow("预览尺寸", "${previewSize.widthDp}×${previewSize.heightDp} dp")
                }
            }
            if (scenario == QuotaWidgetFixtureScenario.THRESHOLD_COLORS) {
                SettingsSection("阈值颜色") {
                    SettingsGroup(allowLiquidOverflow = true) {
                        SettingsSegmentedSelector(
                            options = thresholdOptions,
                            selectedValue = thresholdPercent,
                            enabled = true,
                            onSelected = { thresholdPercent = it },
                        )
                        SettingsInfoRow("当前百分比", "$thresholdPercent%")
                    }
                }
            }
            SettingsSection("正式 Widget 预览") {
                SettingsGroup(allowLiquidOverflow = true) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 12.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        AndroidView(
                            factory = { context -> FrameLayout(context) },
                            modifier = Modifier
                                .width(previewSize.widthDp.dp)
                                .height(previewSize.heightDp.dp),
                            update = { container ->
                                container.removeAllViews()
                                val remoteViews = QuotaWidgetRenderer.createPreviewRemoteViews(
                                    context = container.context,
                                    projection = projection,
                                )
                                val widgetView = remoteViews.apply(container.context, container)
                                container.addView(
                                    widgetView,
                                    FrameLayout.LayoutParams(
                                        ViewGroup.LayoutParams.MATCH_PARENT,
                                        ViewGroup.LayoutParams.MATCH_PARENT,
                                    ),
                                )
                            },
                        )
                    }
                }
            }
            if (previewSize.widthDp == 300 && previewSize.heightDp == 110) {
                Text(
                    "Provider 最小尺寸；Activity 内 RemoteViews 预览可能不完全等同 Launcher 实际尺寸。",
                    modifier = Modifier.padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
                    color = palette.color(palette.secondary),
                )
            }
            Text(
                "直接复用正式 widget_quota.xml 与 RemoteViews renderer；只渲染本地 fake projection，不会联网、写入 widget store、更新桌面组件、发送广播或打开主页面。",
                modifier = Modifier.padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
                color = palette.color(palette.secondary),
            )
        }
    }
}
