package com.codexquotatray.android

import android.app.Activity
import android.content.Intent
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.core.view.ViewCompat
import com.codexquotatray.android.auth.OAuthLoginUpdate
import com.codexquotatray.android.quota.CodexQuotaRepository
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import java.util.concurrent.Executors

class LoginActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }
    private val worker = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val repository by lazy { CodexQuotaRepository(this) }
    private var verificationUrl: String? = null
    private var busy = false

    private lateinit var statusView: TextView
    private lateinit var openBrowserButton: Button
    private lateinit var loginButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        val root = buildContent()
        setContentView(root)
        ViewCompat.requestApplyInsets(root)
        beginLogin()
    }

    private fun buildContent(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(22), dp(20), dp(18))
            setBackgroundColor(palette.background)
        }
        AppTheme.installTopSafePadding(root)
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(18), 0, dp(18))
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
            textView("登录 Codex", 24f, Typeface.BOLD).apply {
                setTextColor(palette.title)
            },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
        )
        content.addView(toolbar, marginParams(bottom = 28))

        statusView = textView("正在准备登录…", 16f, Typeface.NORMAL).apply {
            setTextColor(palette.secondary)
            setLineSpacing(dp(4).toFloat(), 1.0f)
        }
        content.addView(statusView, marginParams(bottom = 20))

        openBrowserButton = actionButton("打开浏览器") { openVerificationBrowser() }
        openBrowserButton.visibility = View.GONE
        content.addView(openBrowserButton, marginParams(bottom = 10))

        loginButton = actionButton("重新登录") { beginLogin() }
        content.addView(loginButton)

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

    private fun beginLogin() {
        if (busy) return
        busy = true
        AppLogStore.record(this, "登录流程开始")
        verificationUrl = null
        statusView.text = "正在准备登录…"
        statusView.setTextColor(palette.secondary)
        openBrowserButton.visibility = View.GONE
        loginButton.text = "登录处理中…"
        loginButton.isEnabled = false
        worker.execute {
            val result = runCatching {
                repository.login { update ->
                    mainHandler.post { renderLoginUpdate(update) }
                }
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
                        AppLogStore.record(
                            this@LoginActivity,
                            "登录失败：${error.message ?: "未知错误"}",
                            "WARN",
                        )
                        statusView.text = error.message ?: "登录失败，请重试"
                        statusView.setTextColor(palette.error)
                        loginButton.text = "重新登录"
                        loginButton.isEnabled = true
                        openBrowserButton.visibility = View.GONE
                    },
                )
            }
        }
    }

    private fun renderLoginUpdate(update: OAuthLoginUpdate) {
        verificationUrl = update.verificationUrl ?: verificationUrl
        if (update.state != "waiting_for_user") {
            statusView.text = when (update.state) {
                "login_starting" -> "正在准备登录…"
                "exchanging_token" -> "登录完成，正在保存登录状态…"
                else -> update.message ?: "正在处理登录…"
            }
            return
        }
        val details = buildString {
            append("请在浏览器完成 Codex 登录")
            update.userCode?.let { append("\n登录码：$it") }
            if (!verificationUrl.isNullOrBlank()) append("\n然后点击下方按钮")
        }
        statusView.text = details
        statusView.setTextColor(palette.body)
        openBrowserButton.visibility = if (verificationUrl.isNullOrBlank()) View.GONE else View.VISIBLE
        openBrowserButton.isEnabled = !verificationUrl.isNullOrBlank()
    }

    private fun openVerificationBrowser() {
        val url = verificationUrl ?: return
        val uri = runCatching { Uri.parse(url) }.getOrNull()
        if (uri?.scheme != "https") {
            statusView.text = "登录地址无效，请重新开始登录"
            return
        }
        runCatching {
            startActivity(Intent(Intent.ACTION_VIEW, uri))
        }.onFailure {
            statusView.text = "没有可用的浏览器，请手动打开登录地址"
        }
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

    override fun onDestroy() {
        worker.shutdownNow()
        super.onDestroy()
    }
}
