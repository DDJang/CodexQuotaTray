package com.codexquotatray.android

import android.content.Intent
import android.content.res.Configuration
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import com.codexquotatray.android.alerts.QuotaAlertStateStore
import com.codexquotatray.android.auth.CodexProcessLock
import com.codexquotatray.android.auth.JwtClaims
import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.usage.TokenUsageRefreshScheduler
import com.codexquotatray.android.usage.TokenUsageCache

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
            val palette = settingsPalette(AppTheme.palette(this), AppTheme.effectiveMode(this))
            CodexQuotaTheme(palette) {
                SecondaryScreenScaffold(title = "OpenAI 账号", onBack = ::finish) {
                    Column(Modifier.fillMaxWidth()) {
                        SettingsSection("OpenAI") {
                            SettingsGroup(allowLiquidOverflow = true) {
                                if (credentials == null) {
                                    SettingsInfoRow("状态", "尚未登录 OpenAI")
                                } else {
                                    credentials?.let { value ->
                                        JwtClaims.planType(value.idToken)
                                            ?.takeIf(String::isNotBlank)
                                            ?.let { plan ->
                                                SettingsInfoRow(
                                                    title = "账户类型",
                                                    value = plan.replaceFirstChar { character -> character.uppercase() },
                                                )
                                            }
                                        value.accountId?.let { accountId ->
                                            SettingsDivider()
                                            SettingsInfoRow("账号标识", mask(accountId))
                                        }
                                    }
                                }
                                SettingsActionButton(
                                    label = if (credentials == null) "登录 OpenAI" else "退出登录",
                                    danger = credentials != null,
                                    bottomPadding = SettingsUiTokens.actionEdgeInset,
                                    onClick = {
                                        if (credentials == null) openLogin() else showLogoutDialog = true
                                    },
                                )
                            }
                        }
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

    private fun mask(value: String): String =
        if (value.length <= 8) "••••" else "${value.take(4)}…${value.takeLast(4)}"

    private fun openLogin() { startActivity(Intent(this, LoginActivity::class.java)) }

    private fun logout() {
        synchronized(CodexProcessLock.monitor) {
            oauthStore.clear()
            QuotaAlertStateStore(this).clear()
            QuotaSnapshotStore(this).clear()
            TokenUsageCache(this).clear()
        }
        com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(this)
        QuotaRefreshScheduler.schedule(this)
        TokenUsageRefreshScheduler.schedule(this)
        AppLogStore.record(this, "已退出登录")
        render()
        setResult(RESULT_OK)
    }
}
