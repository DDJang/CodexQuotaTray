package com.codexquotatray.android

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.content.res.Configuration
import android.os.Handler
import android.os.Looper
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.auth.OAuthLoginUpdate
import com.codexquotatray.android.quota.CodexQuotaRepository
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import java.util.concurrent.Executors

class LoginActivity : ComponentActivity() {
    private val worker = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val repository by lazy { CodexQuotaRepository(this) }
    private var verificationUrl: String? by mutableStateOf(null)
    private var statusText by mutableStateOf("正在准备登录…")
    private var userCode: String? by mutableStateOf(null)
    private var busy by mutableStateOf(false)
    private var failed by mutableStateOf(false)
    private var themeVersion by mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContent {
            themeVersion
            val palette = AppTheme.palette(this)
            CodexQuotaTheme(palette) {
                SecondaryScreenScaffold(title = "登录 Codex", onBack = ::finish) {
                    Column(
                        Modifier.fillMaxWidth().padding(
                            horizontal = CodexDimensions.screenPadding,
                            vertical = 20.dp,
                        ),
                    ) {
                        CodexCard(Modifier.fillMaxWidth()) {
                            Text(
                                statusText,
                                modifier = Modifier.fillMaxWidth(),
                                color = palette.color(if (failed) palette.error else palette.secondary),
                                fontSize = 16.sp,
                                lineHeight = 23.sp,
                                textAlign = TextAlign.Center,
                            )
                            userCode?.let { code ->
                                Text(
                                    code,
                                    modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
                                    color = palette.color(palette.accent),
                                    fontSize = 25.sp,
                                    fontWeight = FontWeight.Bold,
                                    fontFamily = FontFamily.Monospace,
                                    letterSpacing = 2.sp,
                                    textAlign = TextAlign.Center,
                                )
                            }
                        }
                        if (!verificationUrl.isNullOrBlank()) {
                            CodexButton(
                                text = "打开浏览器",
                                onClick = ::openVerificationBrowser,
                                modifier = Modifier.fillMaxWidth().padding(top = 14.dp),
                                style = CodexButtonStyle.PRIMARY,
                            )
                        }
                        if (!busy) {
                            CodexButton(
                                text = "重新登录",
                                onClick = ::beginLogin,
                                modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                            )
                        }
                    }
                }
            }
        }
        beginLogin()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (AppTheme.mode(this) == ThemeMode.SYSTEM) themeVersion++
        AppTheme.applySystemBars(this)
    }

    private fun beginLogin() {
        if (busy) return
        busy = true
        failed = false
        AppLogStore.record(this, "登录流程开始")
        verificationUrl = null
        userCode = null
        statusText = "正在准备登录…"
        worker.execute {
            val result = runCatching {
                repository.login { update -> mainHandler.post { renderLoginUpdate(update) } }
            }
            mainHandler.post {
                busy = false
                result.fold(
                    onSuccess = {
                        AppLogStore.record(this@LoginActivity, "登录成功")
                        QuotaRefreshScheduler.schedule(this)
                        setResult(RESULT_OK)
                        finish()
                    },
                    onFailure = { error ->
                        AppLogStore.record(this@LoginActivity, "登录失败：${error.message ?: "未知错误"}", "WARN")
                        statusText = error.message ?: "登录失败，请重试"
                        failed = true
                        verificationUrl = null
                        userCode = null
                    },
                )
            }
        }
    }

    private fun renderLoginUpdate(update: OAuthLoginUpdate) {
        verificationUrl = update.verificationUrl ?: verificationUrl
        if (update.state != "waiting_for_user") {
            userCode = null
            statusText = when (update.state) {
                "login_starting" -> "正在准备登录…"
                "exchanging_token" -> "登录完成，正在保存登录状态…"
                else -> update.message ?: "正在处理登录…"
            }
            return
        }
        statusText = "请在浏览器完成 Codex 登录"
        userCode = update.userCode
    }

    private fun openVerificationBrowser() {
        val url = verificationUrl ?: return
        val uri = runCatching { Uri.parse(url) }.getOrNull()
        if (uri?.scheme != "https") {
            statusText = "登录地址无效，请重新开始登录"
            failed = true
            return
        }
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, uri)) }.onFailure {
            statusText = "没有可用的浏览器，请手动打开登录地址"
            failed = true
        }
    }

    override fun onDestroy() {
        worker.shutdownNow()
        super.onDestroy()
    }
}
