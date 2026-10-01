package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.DashboardScaffold
import com.codexquotatray.android.LiquidDialogSurface
import com.codexquotatray.android.LiquidModalOverlay
import com.codexquotatray.android.LocalQuotaPalette
import com.codexquotatray.android.QuotaPageContent
import com.codexquotatray.android.RefreshStatusFormatter
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.TokenUsagePageContent
import com.codexquotatray.android.color
import com.codexquotatray.android.protocol.QuotaSource
import com.codexquotatray.android.protocol.ResetCredit
import com.codexquotatray.android.ui.QuotaCardModel
import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.ResetCreditDetailState
import com.codexquotatray.android.ui.ResetCreditUiModel
import com.codexquotatray.android.usage.DataTransport
import com.codexquotatray.android.usage.TokenUsageDay
import com.codexquotatray.android.usage.TokenUsageScope
import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSummary
import java.time.Instant
import java.time.LocalDate

/** Uses the production dashboard with only anonymous in-memory data and local callbacks. */
class DashboardScrollFixtureActivity : ComponentActivity() {
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
        setContent {
            CodexQuotaTheme(AppTheme.palette(this, selectedTheme)) {
                DashboardScrollFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun DashboardScrollFixtureScreen(onBack: () -> Unit) {
    var selectedIndex by remember { mutableIntStateOf(0) }
    var longContent by remember { mutableStateOf(true) }
    var showControls by remember { mutableStateOf(true) }
    var localActionCount by remember { mutableIntStateOf(0) }
    val now = remember { Instant.now() }
    val quota = remember(now, longContent) { dashboardQuotaFixture(now, longContent) }
    val token = remember(now) { dashboardTokenFixture(now) }
    val palette = LocalQuotaPalette.current

    // Reset both pages' scroll positions when changing the content length.
    key(longContent) {
        DashboardScaffold(
            selectedIndex = selectedIndex,
            onSelected = { selectedIndex = it },
            onSettings = { showControls = true },
            actionEnabled = true,
            actionBusy = false,
            onAction = { localActionCount++ },
            modalContent = { backdrop ->
                if (showControls) {
                    LiquidModalOverlay(
                        paneTitle = "首页滚动预览",
                        onDismiss = { showControls = false },
                    ) {
                        LiquidDialogSurface(backdrop = backdrop) {
                            Text(
                                "首页滚动预览",
                                Modifier.padding(horizontal = 20.dp),
                                color = palette.color(palette.title),
                            )
                            SettingsSegmentedSelector(
                                options = listOf(
                                    SettingsSegmentOption(0, "长内容"),
                                    SettingsSegmentOption(1, "短内容"),
                                ),
                                selectedValue = if (longContent) 0 else 1,
                                enabled = true,
                                onSelected = { longContent = it == 0 },
                            )
                            Text(
                                "长内容：双额度窗口 + 4 张重置卡、完整统计。\n" +
                                    "短内容：单额度窗口、统计无来源提示。\n" +
                                    "底栏切换额度 / 统计；右上角重新打开场景选择。\n" +
                                    "检查顶部渐变、底部最后一行、上下边缘回弹。\n" +
                                    "刷新只计数：$localActionCount 次；全部为离线假数据。",
                                Modifier.padding(horizontal = 20.dp),
                                color = palette.color(palette.secondary),
                            )
                            TextButton(onClick = { showControls = false }) { Text("开始预览") }
                            TextButton(onClick = onBack) { Text("返回设置") }
                        }
                    }
                }
            },
        ) { pageIndex ->
            if (pageIndex == 0) {
                QuotaPageContent(
                    model = quota,
                    busy = false,
                    onLogin = { localActionCount++ },
                    onPairing = { localActionCount++ },
                )
            } else {
                TokenUsagePageContent(
                    status = if (longContent) {
                        RefreshStatusFormatter.loaded("Windows", "12:00")
                    } else {
                        RefreshStatusFormatter.tokenUnpaired()
                    },
                    paired = longContent,
                    snapshot = if (longContent) token else null,
                    onPairing = { localActionCount++ },
                    onLoginOpenAi = { localActionCount++ },
                )
            }
        }
    }
}

private fun dashboardQuotaFixture(now: Instant, longContent: Boolean): QuotaUiModel {
    val windows = listOf(
        QuotaCardModel(
            title = "5 小时额度",
            remainingPercent = 18,
            usedPercent = 82,
            windowDurationMins = 300L,
            resetsAt = now.epochSecond + 17 * 60L,
        ),
        QuotaCardModel(
            title = "7 天额度",
            remainingPercent = 62,
            usedPercent = 38,
            windowDurationMins = 10_080L,
            resetsAt = now.epochSecond + (2 * 24 + 15) * 3_600L,
        ),
    )
    return QuotaUiModel(
        status = QuotaUiStatus.LOADED,
        windows = if (longContent) windows else windows.take(1),
        resetCredits = if (longContent) {
            ResetCreditUiModel(
                availableCount = 4L,
                availableCredits = listOf(60L, 82L, 512L, 676L).mapIndexed { index, hours ->
                    ResetCredit(
                        id = "fixture-reset-$index",
                        status = "available",
                        expiresAt = now.epochSecond + hours * 3_600L,
                    )
                },
                detailState = ResetCreditDetailState.COMPLETE,
            )
        } else {
            null
        },
        updatedAtMillis = now.toEpochMilli(),
        source = QuotaSource.WINDOWS,
    )
}

private fun dashboardTokenFixture(now: Instant): TokenUsageSnapshot {
    val today = LocalDate.now()
    val days = (0 until 30).map { index ->
        val total = 32_000L + index * 420L
        TokenUsageDay(
            date = today.minusDays((29 - index).toLong()),
            totalTokens = total,
            inputTokens = total / 2,
            cachedInputTokens = total / 10,
            outputTokens = total / 4,
            reasoningTokens = total - total / 2 - total / 10 - total / 4,
        )
    }
    return TokenUsageSnapshot(
        schemaVersion = 1,
        generatedAtUtc = now.toString(),
        sourceTimeZone = "Asia/Shanghai",
        summary = TokenUsageSummary(
            todayTokens = days.last().totalTokens,
            last7DaysTokens = days.takeLast(7).sumOf { it.totalTokens },
            last30DaysTokens = days.sumOf { it.totalTokens },
            lifetimeTokens = 12_500_000L,
            peakDailyTokens = 74_000L,
            peakDate = today,
            activeDays = 30,
            currentStreak = 30,
            longestStreak = 30,
        ),
        days = days,
        transport = DataTransport.WINDOWS,
        scope = TokenUsageScope.LOCAL,
        source = "debug-fixture",
    )
}
