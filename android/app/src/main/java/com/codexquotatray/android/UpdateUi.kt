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

@Composable
internal fun UpdateAvailableDialog(
    release: UpdateRelease,
    currentVersion: String,
    downloading: Boolean,
    progressPercent: Int?,
    onLater: () -> Unit,
    onDownload: () -> Unit,
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
                if (release.notes.isNotBlank()) {
                    Spacer(Modifier.height(10.dp))
                    ReleaseNotesMarkdownView(release.notes)
                }
                if (downloading) {
                    Spacer(Modifier.height(12.dp))
                    Row(Modifier.fillMaxWidth()) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Text(
                            progressPercent?.let { "正在下载更新… $it%" } ?: "正在下载更新…",
                            modifier = Modifier.padding(start = 10.dp),
                            fontSize = 13.sp,
                        )
                    }
                }
            }
        },
        dismissButton = {
            TextButton(onClick = onLater, enabled = !downloading) { Text("稍后") }
        },
        confirmButton = {
            TextButton(onClick = onDownload, enabled = !downloading) { Text("下载更新") }
        },
    )
}
