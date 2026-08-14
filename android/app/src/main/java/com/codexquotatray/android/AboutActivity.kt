package com.codexquotatray.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.content.res.Configuration
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Text
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue

class AboutActivity : ComponentActivity() {
    private var themeVersion by mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContent {
            themeVersion
            val palette = AppTheme.palette(this)
            CodexQuotaTheme(palette) {
                SecondaryScreenScaffold(title = "关于", onBack = ::finish) {
                    Column(
                        Modifier.fillMaxWidth().padding(
                            horizontal = CodexDimensions.screenPadding,
                            vertical = 20.dp,
                        ),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Image(
                            painter = painterResource(R.drawable.ic_launcher_mark),
                            contentDescription = "CodexQuota 图标",
                            modifier = Modifier.size(112.dp),
                        )
                        Text(
                            "CodexQuota",
                            modifier = Modifier.padding(top = 22.dp),
                            color = palette.color(palette.title),
                            fontSize = 23.sp,
                            fontWeight = FontWeight.Bold,
                        )
                        Text(
                            "版本 ${installedVersion()}",
                            modifier = Modifier.padding(top = 8.dp),
                            color = palette.color(palette.muted),
                            fontSize = 14.sp,
                        )
                        Text(
                            PROJECT_URL,
                            modifier = Modifier.padding(top = 18.dp).clickable(onClick = rememberSystemHapticClick(::openProjectPage)),
                            color = palette.color(palette.accent),
                            fontSize = 14.sp,
                        )
                    }
                }
            }
        }
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (AppTheme.mode(this) == ThemeMode.SYSTEM) themeVersion++
        AppTheme.applySystemBars(this)
    }

    @Suppress("DEPRECATION")
    private fun installedVersion(): String =
        packageManager.getPackageInfo(packageName, 0).versionName ?: "未知"

    private fun openProjectPage() {
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(PROJECT_URL))) }
    }

    companion object {
        private const val PROJECT_URL = "https://github.com/DDJang/CodexQuotaTray"
    }
}
