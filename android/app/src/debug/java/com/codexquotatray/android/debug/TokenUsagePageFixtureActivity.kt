package com.codexquotatray.android.debug

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.codexquotatray.android.AppTheme
import com.codexquotatray.android.CodexQuotaTheme
import com.codexquotatray.android.RefreshStatusFormatter
import com.codexquotatray.android.SecondaryScreenScaffold
import com.codexquotatray.android.SettingsGroup
import com.codexquotatray.android.SettingsInfoRow
import com.codexquotatray.android.SettingsSection
import com.codexquotatray.android.SettingsSegmentOption
import com.codexquotatray.android.SettingsSegmentedSelector
import com.codexquotatray.android.SettingsUiTokens
import com.codexquotatray.android.ThemeMode
import com.codexquotatray.android.TokenUsagePageContent
import com.codexquotatray.android.color
import com.codexquotatray.android.settingsPalette
import com.codexquotatray.android.usage.DataTransport
import com.codexquotatray.android.usage.TokenUsageDay
import com.codexquotatray.android.usage.TokenUsageScope
import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSummary
import java.time.LocalDate

private val TOKEN_FIXTURE_TODAY = LocalDate.of(2026, 9, 2)
private const val TOKEN_FIXTURE_GENERATED_AT = "2026-09-02T04:00:00Z"

private enum class TokenFixtureScenario(
    val selectorLabel: String,
    val displayLabel: String,
) {
    UNPAIRED("未配对", "Unpaired"),
    LOADED_TYPICAL("常规", "Loaded typical"),
    SPARSE_HISTORY("稀疏", "Sparse history"),
    LARGE_NUMBERS("大数值", "Large numbers"),
    MISSING_CATEGORY_BREAKDOWN("无分类", "Missing category breakdown"),
    ERROR_STALE_WITH_SNAPSHOT("失败缓存", "Error / stale with snapshot"),
}

private val tokenFixtureOptions = TokenFixtureScenario.entries.mapIndexed { index, scenario ->
    SettingsSegmentOption(index, scenario.selectorLabel)
}

private fun tokenFixtureDay(
    date: LocalDate,
    totalTokens: Long,
    withCategoryBreakdown: Boolean = true,
): TokenUsageDay {
    if (!withCategoryBreakdown) {
        return TokenUsageDay(date, totalTokens, null, null, null, null)
    }
    val input = totalTokens / 2L
    val cachedInput = totalTokens / 10L
    val output = totalTokens / 4L
    val reasoning = totalTokens - input - cachedInput - output
    return TokenUsageDay(date, totalTokens, input, cachedInput, output, reasoning)
}

private val tokenFixtureTypicalDays = (0 until 30).map { index ->
    tokenFixtureDay(
        date = TOKEN_FIXTURE_TODAY.minusDays((29 - index).toLong()),
        totalTokens = 32_000L + index * 420L,
    )
}

private val tokenFixtureSparseDays = listOf(
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(27), 12_400L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(20), 42_000L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(12), 8_600L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(5), 74_000L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(1), 29_500L),
)

private val tokenFixtureLargeDays = listOf(
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(6), 1_200_000_000L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(4), 8_750_000_000L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY.minusDays(2), 42_000_000_000L),
    tokenFixtureDay(TOKEN_FIXTURE_TODAY, 125_000_000_000L),
)

private val tokenFixtureMissingCategoryDays = (0 until 7).map { index ->
    tokenFixtureDay(
        date = TOKEN_FIXTURE_TODAY.minusDays((6 - index).toLong()),
        totalTokens = 18_000L + index * 2_000L,
        withCategoryBreakdown = false,
    )
}

private fun tokenFixtureSummary(
    today: Long?,
    last7Days: Long?,
    last30Days: Long?,
    lifetime: Long?,
    peak: Long?,
    currentStreak: Int?,
    longestStreak: Int?,
): TokenUsageSummary = TokenUsageSummary(
    todayTokens = today,
    last7DaysTokens = last7Days,
    last30DaysTokens = last30Days,
    lifetimeTokens = lifetime,
    peakDailyTokens = peak,
    peakDate = peak?.let { TOKEN_FIXTURE_TODAY },
    activeDays = currentStreak,
    currentStreak = currentStreak,
    longestStreak = longestStreak,
)

private fun tokenFixtureSnapshot(
    summary: TokenUsageSummary,
    days: List<TokenUsageDay>,
): TokenUsageSnapshot = TokenUsageSnapshot(
    schemaVersion = 1,
    generatedAtUtc = TOKEN_FIXTURE_GENERATED_AT,
    sourceTimeZone = "Asia/Shanghai",
    summary = summary,
    days = days,
    transport = DataTransport.WINDOWS,
    scope = TokenUsageScope.LOCAL,
    source = "debug-fixture",
)

private val tokenFixtureTypical = tokenFixtureSnapshot(
    summary = tokenFixtureSummary(58_000L, 258_000L, 1_035_000L, 12_500_000L, 74_000L, 6, 18),
    days = tokenFixtureTypicalDays,
)

private val tokenFixtureSparse = tokenFixtureSnapshot(
    summary = tokenFixtureSummary(29_500L, 154_500L, 166_500L, 1_250_000L, 74_000L, 2, 5),
    days = tokenFixtureSparseDays,
)

private val tokenFixtureLarge = tokenFixtureSnapshot(
    summary = tokenFixtureSummary(125_000_000_000L, 176_950_000_000L, 176_950_000_000L, 9_876_543_210_987L, 125_000_000_000L, 4, 11),
    days = tokenFixtureLargeDays,
)

private val tokenFixtureMissingCategoryBreakdown = tokenFixtureSnapshot(
    summary = tokenFixtureSummary(30_000L, 126_000L, 126_000L, null, 22_000L, 7, 7),
    days = tokenFixtureMissingCategoryDays,
)

private data class TokenFixturePresentation(
    val paired: Boolean,
    val status: String,
    val snapshot: TokenUsageSnapshot?,
)

private fun tokenFixturePresentation(scenario: TokenFixtureScenario): TokenFixturePresentation = when (scenario) {
    TokenFixtureScenario.UNPAIRED -> TokenFixturePresentation(
        paired = false,
        status = RefreshStatusFormatter.tokenUnpaired(),
        snapshot = null,
    )
    TokenFixtureScenario.LOADED_TYPICAL -> TokenFixturePresentation(
        paired = true,
        status = RefreshStatusFormatter.loaded("Windows", "12:00"),
        snapshot = tokenFixtureTypical,
    )
    TokenFixtureScenario.SPARSE_HISTORY -> TokenFixturePresentation(
        paired = true,
        status = RefreshStatusFormatter.loaded("Windows", "12:00"),
        snapshot = tokenFixtureSparse,
    )
    TokenFixtureScenario.LARGE_NUMBERS -> TokenFixturePresentation(
        paired = true,
        status = RefreshStatusFormatter.loaded("Windows", "12:00"),
        snapshot = tokenFixtureLarge,
    )
    TokenFixtureScenario.MISSING_CATEGORY_BREAKDOWN -> TokenFixturePresentation(
        paired = true,
        status = RefreshStatusFormatter.loaded("Windows", "12:00"),
        snapshot = tokenFixtureMissingCategoryBreakdown,
    )
    TokenFixtureScenario.ERROR_STALE_WITH_SNAPSHOT -> TokenFixturePresentation(
        paired = true,
        status = RefreshStatusFormatter.tokenFailure("网络不可用", "12:00"),
        snapshot = tokenFixtureTypical,
    )
}

class TokenUsagePageFixtureActivity : ComponentActivity() {
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
                TokenUsagePageFixtureScreen(onBack = ::finish)
            }
        }
    }
}

@Composable
private fun TokenUsagePageFixtureScreen(onBack: () -> Unit) {
    var selectedScenario by remember { mutableIntStateOf(TokenFixtureScenario.UNPAIRED.ordinal) }
    var localActionCount by remember { mutableIntStateOf(0) }
    val scenario = TokenFixtureScenario.entries[selectedScenario]
    val presentation = tokenFixturePresentation(scenario)
    val palette = com.codexquotatray.android.LocalQuotaPalette.current

    SecondaryScreenScaffold(title = "Token 使用页面", onBack = onBack) {
        Column(Modifier.fillMaxWidth()) {
            SettingsSection("Debug 场景") {
                SettingsGroup(allowLiquidOverflow = true) {
                    SettingsSegmentedSelector(
                        options = tokenFixtureOptions,
                        selectedValue = selectedScenario,
                        enabled = true,
                        onSelected = { selectedScenario = it },
                    )
                    SettingsInfoRow("当前场景", scenario.displayLabel, valueMaxLines = 2)
                    SettingsInfoRow("本地操作", "$localActionCount 次")
                }
            }
            TokenUsagePageContent(
                status = presentation.status,
                paired = presentation.paired,
                snapshot = presentation.snapshot,
                onPairing = { localActionCount++ },
                modifier = Modifier.fillMaxWidth(),
            )
            Text(
                "仅展示本地 fake Token 状态；不会扫码、连接 Windows、访问网络、读取缓存或写入 pairing store。",
                modifier = Modifier.padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
                color = palette.color(palette.secondary),
            )
        }
    }
}
