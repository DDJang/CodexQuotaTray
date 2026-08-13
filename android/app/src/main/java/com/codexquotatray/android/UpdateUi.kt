package com.codexquotatray.android

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.update.UpdateRelease
import com.codexquotatray.android.update.UpdateDownloadFormatting
import com.codexquotatray.android.update.UpdateDownloadProgress

@Composable
internal fun UpdateAvailableDialog(
    release: UpdateRelease,
    currentVersion: String,
    downloading: Boolean,
    progress: UpdateDownloadProgress,
    downloadError: String?,
    onLater: () -> Unit,
    onDownload: () -> Unit,
    onCancel: () -> Unit,
    onBrowserDownload: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    AlertDialog(
        onDismissRequest = if (downloading) ({}) else onLater,
        containerColor = palette.color(palette.surface),
        titleContentColor = palette.color(palette.title),
        textContentColor = palette.color(palette.body),
        title = { Text("发现新版本 ${release.version}") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text("当前版本  $currentVersion", fontSize = 14.sp)
                Text("最新版本  ${release.version}", fontSize = 14.sp)
                downloadError?.let { error ->
                    Spacer(Modifier.height(10.dp))
                    Text("下载失败\n$error", fontSize = 13.sp)
                }
                if (release.notes.isNotBlank()) {
                    Spacer(Modifier.height(10.dp))
                    ReleaseNotesMarkdownView(release.notes)
                }
                if (downloading) {
                    Spacer(Modifier.height(12.dp))
                    Text(
                        if (progress.phase == com.codexquotatray.android.update.UpdateDownloadPhase.VERIFYING) {
                            "正在校验安装包…"
                        } else {
                            "正在下载 ${release.version}"
                        },
                        fontSize = 13.sp,
                    )
                    Text(
                        UpdateDownloadFormatting.formatProgress(progress),
                        fontSize = 13.sp,
                    )
                    Row(Modifier.fillMaxWidth()) {
                        val percentage = progress.percentage
                        if (percentage == null) {
                            CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        } else {
                            androidx.compose.material3.LinearProgressIndicator(
                                progress = { percentage / 100f },
                                modifier = Modifier.fillMaxWidth().weight(1f),
                            )
                        }
                        Text(
                            if (percentage == null) "正在处理…" else "$percentage%",
                            modifier = Modifier.padding(start = 10.dp),
                            fontSize = 12.sp,
                        )
                    }
                }
                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = androidx.compose.foundation.layout.Arrangement.SpaceBetween,
                ) {
                    TextButton(onClick = onBrowserDownload) { Text("浏览器下载") }
                    if (downloading) {
                        TextButton(onClick = onCancel) { Text("取消") }
                    }
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onLater, enabled = !downloading) { Text("稍后") }
        },
        confirmButton = {
            TextButton(onClick = onDownload, enabled = !downloading) {
                Text(if (downloadError == null) "下载并安装" else "重试")
            }
        },
    )
}
