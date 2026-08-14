package com.codexquotatray.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.res.Configuration
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import java.util.Locale

private data class LogDisplayEntry(
    val time: String,
    val level: String,
    val message: String,
)

private val LOG_ENTRY_PATTERN = Regex(
    """^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+\[([^]]+)]\s+(.*)$""",
)

private fun parseLogEntries(raw: String): List<LogDisplayEntry> {
    if (raw == "暂无日志") return emptyList()
    return raw.lineSequence()
        .filter(String::isNotBlank)
        .map { line ->
            val match = LOG_ENTRY_PATTERN.matchEntire(line)
            if (match == null) {
                LogDisplayEntry("未知时间", "INFO", line)
            } else {
                LogDisplayEntry(
                    time = match.groupValues[1].substring(5, 16),
                    level = match.groupValues[2].uppercase(Locale.ROOT),
                    message = match.groupValues[3],
                )
            }
        }
        .toList()
        .asReversed()
}

class LogActivity : ComponentActivity() {
    private val logStore by lazy { AppLogStore(this) }
    private var logEntries by mutableStateOf<List<LogDisplayEntry>>(emptyList())
    private var visibleLogCount by mutableIntStateOf(INITIAL_VISIBLE_LOG_COUNT)
    private var copied by mutableStateOf(false)
    private var showClearDialog by mutableStateOf(false)
    private var themeVersion by mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        renderLog()
        setContent {
            themeVersion
            val palette = settingsPalette(AppTheme.palette(this), AppTheme.effectiveMode(this))
            CodexQuotaTheme(palette) {
                LaunchedEffect(copied) {
                    if (copied) {
                        delay(COPY_FEEDBACK_MILLIS)
                        copied = false
                    }
                }
                SecondaryScreenScaffold(title = "日志", onBack = ::finish) {
                    Column(
                        Modifier.fillMaxWidth(),
                        verticalArrangement = Arrangement.spacedBy(28.dp),
                    ) {
                        SettingsSection("日志管理") {
                            SettingsGroup {
                                SettingsActionButton(
                                    label = if (copied) "已复制" else "复制全部日志",
                                    enabled = !copied,
                                    topPadding = SettingsUiTokens.actionEdgeInset,
                                    onClick = ::copyLogs,
                                )
                                SettingsActionButton(
                                    label = "清空日志",
                                    danger = true,
                                    bottomPadding = SettingsUiTokens.actionEdgeInset,
                                    onClick = { showClearDialog = true },
                                )
                            }
                        }

                        SettingsSection("最近记录") {
                            SettingsGroup {
                                if (logEntries.isEmpty()) {
                                    SettingsInfoRow("记录", "暂无日志")
                                } else {
                                    logEntries.take(visibleLogCount).forEachIndexed { index, entry ->
                                        if (index > 0) SettingsDivider()
                                        LogRow(entry)
                                    }
                                    if (visibleLogCount < logEntries.size) {
                                        SettingsActionButton(
                                            label = "显示更早日志",
                                            bottomPadding = SettingsUiTokens.actionEdgeInset,
                                            onClick = {
                                                visibleLogCount = (visibleLogCount + LOG_PAGE_SIZE)
                                                    .coerceAtMost(logEntries.size)
                                            },
                                        )
                                    }
                                }
                            }
                        }
                    }
                }
                if (showClearDialog) {
                    CodexConfirmDialog(
                        title = "清空日志",
                        message = "确定清空全部本地日志吗？",
                        confirmText = "清空",
                        onConfirm = { logStore.clear(); renderLog() },
                        onDismiss = { showClearDialog = false },
                    )
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        renderLog()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (AppTheme.mode(this) == ThemeMode.SYSTEM) themeVersion++
        AppTheme.applySystemBars(this)
    }

    private fun renderLog() {
        logEntries = parseLogEntries(logStore.read())
        visibleLogCount = INITIAL_VISIBLE_LOG_COUNT
    }

    private fun copyLogs() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboard?.setPrimaryClip(ClipData.newPlainText("CodexQuota 日志", logStore.read()))
        copied = true
    }

    companion object {
        private const val COPY_FEEDBACK_MILLIS = 1_200L
        private const val INITIAL_VISIBLE_LOG_COUNT = 20
        private const val LOG_PAGE_SIZE = 20
    }
}

@Composable
private fun LogRow(entry: LogDisplayEntry) {
    val palette = LocalQuotaPalette.current
    val levelColor = if (entry.level == "WARN" || entry.level == "ERROR") {
        palette.color(palette.error)
    } else {
        palette.color(palette.secondary)
    }
    Column(
        Modifier
            .fillMaxWidth()
            .heightIn(min = SettingsUiTokens.rowMinHeight)
            .padding(
                horizontal = SettingsUiTokens.rowHorizontalPadding,
                vertical = 8.dp,
            ),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Row(Modifier.fillMaxWidth()) {
            Text(entry.time, color = palette.color(palette.muted), fontSize = 12.sp)
            Text(" · ", color = palette.color(palette.muted), fontSize = 12.sp)
            Text(
                entry.level,
                color = levelColor,
                fontSize = 12.sp,
                fontWeight = FontWeight.Medium,
            )
        }
        SelectionContainer {
            Text(
                entry.message,
                modifier = Modifier.fillMaxWidth(),
                color = palette.color(palette.body),
                fontSize = 14.sp,
                lineHeight = 20.sp,
            )
        }
    }
}
