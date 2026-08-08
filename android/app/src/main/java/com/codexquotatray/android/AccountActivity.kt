package com.codexquotatray.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.codexquotatray.android.alerts.QuotaAlertStateStore
import com.codexquotatray.android.auth.JwtClaims
import com.codexquotatray.android.auth.OAuthCredentials
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaSnapshotStore

class AccountActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }
    private val oauthStore by lazy { OAuthStore(this) }
    private lateinit var accountView: TextView
    private lateinit var accountActionButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContentView(buildContent())
        render()
    }

    override fun onResume() {
        super.onResume()
        if (::accountView.isInitialized) render()
    }

    private fun buildContent(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(22), dp(20), dp(18))
            setBackgroundColor(palette.background)
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(8), 0, dp(20))
        }
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            addView(content)
        }

        val toolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        toolbar.addView(backButton())
        toolbar.addView(
            textView("账号管理", 24f, Typeface.BOLD).apply {
                setTextColor(palette.title)
            },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
        )
        content.addView(toolbar, marginParams(bottom = 28))

        content.addView(
            textView("当前账号", 18f, Typeface.BOLD).apply {
            setTextColor(palette.body)
            },
            marginParams(bottom = 10),
        )
        accountView = textView("正在读取账号状态…", 16f, Typeface.NORMAL).apply {
            setTextColor(palette.secondary)
            setLineSpacing(dp(4).toFloat(), 1.0f)
        }
        content.addView(accountView, marginParams(bottom = 24))

        accountActionButton = actionButton("登录 Codex") { openLogin() }
        content.addView(accountActionButton)

        root.addView(
            scroll,
            LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f,
            ),
        )
        return root
    }

    private fun render() {
        val credentials = oauthStore.load()
        if (credentials == null) {
            accountView.text = "尚未登录 Codex"
            accountActionButton.text = "登录 Codex"
            accountActionButton.setOnClickListener { openLogin() }
        } else {
            accountView.text = displayAccount(credentials)
            accountActionButton.text = "退出登录"
            accountActionButton.setOnClickListener { confirmLogout() }
        }
    }

    private fun displayAccount(credentials: OAuthCredentials): String = buildString {
        append("已登录 Codex")
        JwtClaims.planType(credentials.idToken)
            ?.takeIf(String::isNotBlank)
            ?.let { append("\n账户类型：${it.replaceFirstChar { character -> character.uppercase() }}") }
        credentials.accountId?.let { append("\n账号标识：${mask(it)}") }
    }

    private fun mask(value: String): String = if (value.length <= 8) {
        "••••"
    } else {
        "${value.take(4)}…${value.takeLast(4)}"
    }

    private fun openLogin() {
        startActivity(Intent(this, LoginActivity::class.java))
    }

    private fun confirmLogout() {
        AlertDialog.Builder(this)
            .setTitle("退出登录")
            .setMessage("退出后需要重新登录才能读取额度。")
            .setNegativeButton("取消", null)
            .setPositiveButton("退出") { _, _ -> logout() }
            .show()
    }

    private fun logout() {
        oauthStore.clear()
        QuotaAlertStateStore(this).clear()
        QuotaSnapshotStore(this).clear()
        QuotaRefreshScheduler.schedule(this)
        AppLogStore.record(this, "已退出登录")
        render()
        setResult(RESULT_OK)
    }

    private fun backButton(): TextView = textView("‹", 34f, Typeface.NORMAL).apply {
        gravity = Gravity.CENTER
        setTextColor(palette.secondaryButtonText)
        isClickable = true
        setOnClickListener { finish() }
        layoutParams = LinearLayout.LayoutParams(dp(48), dp(48))
    }

    private fun actionButton(text: String, action: () -> Unit): Button = Button(this).apply {
        this.text = text
        textSize = 15f
        isAllCaps = false
        setTypeface(typeface, Typeface.BOLD)
        minimumHeight = dp(52)
        minHeight = dp(52)
        backgroundTintList = android.content.res.ColorStateList.valueOf(palette.secondaryButton)
        setTextColor(palette.secondaryButtonText)
        setOnClickListener { action() }
    }

    private fun textView(text: String, size: Float, style: Int): TextView = TextView(this).apply {
        this.text = text
        textSize = size
        setTypeface(typeface, style)
    }

    private fun marginParams(bottom: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT,
        ).apply {
            setMargins(0, 0, 0, dp(bottom))
        }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt().coerceAtLeast(value)
}
