package com.codexquotatray.android

import android.content.Intent
import android.content.res.Configuration
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.alerts.QuotaAlertStateStore
import com.codexquotatray.android.auth.CodexProcessLock
import com.codexquotatray.android.auth.JwtClaims
import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaSnapshotStore

class AccountActivity : ComponentActivity() {
    private val oauthStore by lazy { OAuthStore(this) }
    private var credentials by mutableStateOf<OAuthCredentials?>(null)
    private var showLogoutDialog by mutableStateOf(false)
    private var themeVersion by mutableIntStateOf(0)

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        render()
        setContent {
            themeVersion
            val palette = AppTheme.palette(this)
            CodexQuotaTheme(palette) {
                SecondaryScreenScaffold(title = "Codex 额度账号", onBack = ::finish) {
                    Column(
                        Modifier.fillMaxSize().padding(
                            horizontal = CodexDimensions.screenPadding,
                            vertical = 20.dp,
                        ),
                        verticalArrangement = Arrangement.spacedBy(16.dp),
                    ) {
                        CodexCard(Modifier.fillMaxWidth()) {
                            Text(
                                "当前登录状态",
                                color = palette.color(palette.title),
                                fontSize = 17.sp,
                                fontWeight = FontWeight.Bold,
                            )
                            Text(
                                credentials?.let(::displayAccount) ?: "尚未登录 Codex",
                                modifier = Modifier.padding(top = 10.dp),
                                color = palette.color(palette.secondary),
                                fontSize = 15.sp,
                                lineHeight = 22.sp,
                            )
                        }
                        CodexButton(
                            text = if (credentials == null) "登录 Codex" else "退出登录",
                            onClick = {
                                if (credentials == null) openLogin() else showLogoutDialog = true
                            },
                            modifier = Modifier.fillMaxWidth(),
                            style = if (credentials == null) CodexButtonStyle.PRIMARY else CodexButtonStyle.DANGER,
                        )
                    }
                }
                if (showLogoutDialog) {
                    CodexConfirmDialog(
                        title = "退出登录",
                        message = "退出后需要重新登录才能读取额度。",
                        confirmText = "退出",
                        onConfirm = ::logout,
                        onDismiss = { showLogoutDialog = false },
                    )
                }
            }
        }
    }

    override fun onResume() { super.onResume(); render() }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        if (AppTheme.mode(this) == ThemeMode.SYSTEM) themeVersion++
        AppTheme.applySystemBars(this)
    }

    private fun render() { credentials = oauthStore.load() }

    private fun displayAccount(value: OAuthCredentials): String = buildString {
        append("已登录 Codex")
        JwtClaims.planType(value.idToken)?.takeIf(String::isNotBlank)?.let {
            append("\n账户类型：${it.replaceFirstChar { character -> character.uppercase() }}")
        }
        value.accountId?.let { append("\n账号标识：${mask(it)}") }
    }

    private fun mask(value: String): String =
        if (value.length <= 8) "••••" else "${value.take(4)}…${value.takeLast(4)}"

    private fun openLogin() { startActivity(Intent(this, LoginActivity::class.java)) }

    private fun logout() {
        synchronized(CodexProcessLock.monitor) {
            oauthStore.clear()
            QuotaAlertStateStore(this).clear()
            QuotaSnapshotStore(this).clear()
        }
        com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(this)
        QuotaRefreshScheduler.schedule(this)
        AppLogStore.record(this, "已退出登录")
        render()
        setResult(RESULT_OK)
    }
}
