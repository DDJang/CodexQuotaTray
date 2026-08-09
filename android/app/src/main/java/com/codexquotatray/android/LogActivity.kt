package com.codexquotatray.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay

class LogActivity : ComponentActivity() {
    private val logStore by lazy { AppLogStore(this) }
    private var logText by mutableStateOf("暂无日志")
    private var copied by mutableStateOf(false)
    private var showClearDialog by mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        renderLog()
        setContent {
            val palette = AppTheme.palette(this)
            CodexQuotaTheme(palette) {
                LaunchedEffect(copied) {
                    if (copied) {
                        delay(COPY_FEEDBACK_MILLIS)
                        copied = false
                    }
                }
                SecondaryScreenScaffold(title = "日志", onBack = ::finish) {
                    Column(
                        Modifier.fillMaxSize().padding(
                            start = CodexDimensions.screenPadding,
                            end = CodexDimensions.screenPadding,
                            top = 12.dp,
                            bottom = 16.dp,
                        ),
                    ) {
                        Text(
                            "这里只显示脱敏后的本地运行摘要，不包含 token、设备码或完整响应。",
                            color = palette.color(palette.muted),
                            fontSize = 13.sp,
                            modifier = Modifier.padding(bottom = 12.dp),
                        )
                        CodexCard(Modifier.fillMaxWidth().weight(1f)) {
                            SelectionContainer {
                                Text(
                                    logText,
                                    modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
                                    color = palette.color(palette.body),
                                    fontSize = 13.sp,
                                    lineHeight = 19.sp,
                                    fontFamily = FontFamily.Monospace,
                                )
                            }
                        }
                        Row(
                            Modifier.fillMaxWidth().padding(top = 12.dp),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            CodexButton(
                                text = if (copied) "已复制" else "复制日志",
                                onClick = ::copyLogs,
                                enabled = !copied,
                                modifier = Modifier.weight(1f),
                            )
                            CodexButton(
                                text = "清空日志",
                                onClick = { showClearDialog = true },
                                modifier = Modifier.weight(1f),
                                style = CodexButtonStyle.DANGER,
                            )
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

    private fun renderLog() { logText = logStore.read() }

    private fun copyLogs() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboard?.setPrimaryClip(ClipData.newPlainText("CodexQuota 日志", logStore.read()))
        copied = true
    }

    companion object { private const val COPY_FEEDBACK_MILLIS = 1_200L }
}
