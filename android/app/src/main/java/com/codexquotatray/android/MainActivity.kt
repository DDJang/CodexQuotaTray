package com.codexquotatray.android

import android.app.Activity
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import com.codexquotatray.android.poc.P0_5Probe
import com.codexquotatray.android.protocol.LoginUpdate
import com.codexquotatray.android.ui.QuotaCardModel
import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.toQuotaUiModel
import java.util.concurrent.Executors

class MainActivity : Activity() {
    private val worker = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private var activeProbe: P0_5Probe? = null
    private var verificationUrl: String? = null
    private var busy = false

    private lateinit var accountView: TextView
    private lateinit var statusView: TextView
    private lateinit var windowsContainer: LinearLayout
    private lateinit var updatedView: TextView
    private lateinit var refreshButton: Button
    private lateinit var loginButton: Button
    private lateinit var openBrowserButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildContent())
        render(QuotaUiModel(QuotaUiStatus.LOADING, message = "正在读取额度…"))
        refresh()
    }

    private fun buildContent(): View {
        val padding = dp(16)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding, padding, padding)
        }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, 0, 0, dp(12))
        }
        val scroll = ScrollView(this).apply { addView(content) }

        content.addView(textView("CodexQuota", 28f, Typeface.BOLD))
        accountView = textView("Codex / 当前账户", 16f, Typeface.NORMAL)
        content.addView(accountView, marginParams(bottom = 12))
        statusView = textView("正在读取额度…", 16f, Typeface.NORMAL)
        content.addView(statusView, marginParams(bottom = 12))
        windowsContainer = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        content.addView(windowsContainer)
        updatedView = textView("尚未更新", 13f, Typeface.NORMAL)
        content.addView(updatedView, marginParams(top = 12))

        root.addView(
            scroll,
            LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f,
            ),
        )

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        refreshButton = button("刷新") { refresh() }
        loginButton = button("登录 Codex") { startLogin() }
        actions.addView(refreshButton, weightParams())
        actions.addView(loginButton, weightParams(left = 8))
        root.addView(actions)

        openBrowserButton = button("打开浏览器") { openVerificationBrowser() }
        openBrowserButton.visibility = View.GONE
        root.addView(openBrowserButton, marginParams(top = 8))
        return root
    }

    private fun refresh() {
        if (busy) return
        busy = true
        verificationUrl = null
        render(QuotaUiModel(QuotaUiStatus.LOADING, message = "正在读取额度…"))
        val probe = P0_5Probe(this)
        activeProbe = probe
        worker.execute {
            val result = runCatching { probe.run() }
            mainHandler.post {
                activeProbe = null
                busy = false
                val model = result.fold(
                    onSuccess = { value ->
                        value.protocol?.toQuotaUiModel()
                            ?: QuotaUiModel(
                                status = QuotaUiStatus.ERROR,
                                message = "Codex runtime 或 App Server 不可用",
                            )
                    },
                    onFailure = { QuotaUiModel(QuotaUiStatus.ERROR, message = "额度读取失败") },
                )
                render(model)
            }
        }
    }

    private fun startLogin() {
        if (busy) return
        busy = true
        verificationUrl = null
        render(QuotaUiModel(QuotaUiStatus.LOADING, message = "正在连接 Codex 登录…"))
        val probe = P0_5Probe(this)
        activeProbe = probe
        worker.execute {
            val result = runCatching {
                probe.runLogin { update ->
                    mainHandler.post { renderLoginUpdate(update) }
                }
            }
            mainHandler.post {
                activeProbe = null
                busy = false
                val model = result.fold(
                    onSuccess = { value ->
                        value.login?.toQuotaUiModel()
                            ?: QuotaUiModel(QuotaUiStatus.ERROR, message = "登录读取失败")
                    },
                    onFailure = { QuotaUiModel(QuotaUiStatus.ERROR, message = "登录失败") },
                )
                render(model)
            }
        }
    }

    private fun render(model: QuotaUiModel) {
        accountView.text = "${model.accountLabel} / 当前账户"
        statusView.text = model.message ?: when (model.status) {
            QuotaUiStatus.LOADING -> "正在读取额度…"
            QuotaUiStatus.UNAUTHENTICATED -> "尚未登录 Codex"
            QuotaUiStatus.LOADED -> "额度读取成功"
            QuotaUiStatus.ERROR -> "额度读取失败"
        }
        statusView.setTextColor(
            if (model.status == QuotaUiStatus.ERROR) Color.rgb(170, 30, 30) else Color.DKGRAY,
        )
        windowsContainer.removeAllViews()
        if (model.status == QuotaUiStatus.LOADED && model.windows.isEmpty()) {
            windowsContainer.addView(textView("当前没有可用额度窗口", 15f, Typeface.NORMAL))
        } else {
            model.windows.forEach { windowsContainer.addView(windowCard(it)) }
        }
        updatedView.text = model.updatedAtMillis?.let { "更新于 ${formatTime(it)}" } ?: "尚未更新"
        refreshButton.isEnabled = !busy
        loginButton.visibility = if (model.status == QuotaUiStatus.UNAUTHENTICATED) {
            View.VISIBLE
        } else {
            View.GONE
        }
        loginButton.isEnabled = !busy
        openBrowserButton.visibility = View.GONE
        openBrowserButton.isEnabled = false
    }

    private fun renderLoginUpdate(update: LoginUpdate) {
        verificationUrl = update.verificationUrl ?: verificationUrl
        if (update.state != "waiting_for_user") {
            statusView.text = when (update.state) {
                "login_starting" -> "正在准备登录…"
                "authenticated" -> "登录成功，正在读取额度…"
                else -> "正在处理登录…"
            }
            return
        }
        val details = buildString {
            append("请在浏览器完成 Codex 登录")
            update.userCode?.let { append("\n登录码：$it") }
            if (!verificationUrl.isNullOrBlank()) append("\n然后点击“打开浏览器”")
        }
        statusView.text = details
        statusView.setTextColor(Color.DKGRAY)
        openBrowserButton.visibility = if (verificationUrl.isNullOrBlank()) View.GONE else View.VISIBLE
        openBrowserButton.isEnabled = !verificationUrl.isNullOrBlank()
        refreshButton.isEnabled = false
        loginButton.visibility = View.GONE
    }

    private fun windowCard(window: QuotaCardModel): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(12), dp(12), dp(12), dp(12))
        background = GradientDrawable().apply {
            setColor(Color.rgb(245, 247, 250))
            cornerRadius = dp(10).toFloat()
        }
        addView(textView(window.title, 18f, Typeface.BOLD))
        addView(
            textView("剩余 ${window.remainingPercent}%", 24f, Typeface.BOLD),
            marginParams(top = 6),
        )
        addView(ProgressBar(context, null, android.R.attr.progressBarStyleHorizontal).apply {
            max = 100
            progress = window.remainingPercent
            contentDescription = "剩余 ${window.remainingPercent}%"
        }, marginParams(top = 6))
        addView(
            textView(formatResetAt(window.resetsAt), 14f, Typeface.NORMAL),
            marginParams(top = 6),
        )
    }.also {
        it.layoutParams = marginParams(bottom = 10)
    }

    private fun openVerificationBrowser() {
        val url = verificationUrl ?: return
        val uri = runCatching { Uri.parse(url) }.getOrNull()
        if (uri?.scheme != "https") {
            statusView.text = "登录地址无效，请重新点击登录"
            return
        }
        runCatching {
            startActivity(Intent(Intent.ACTION_VIEW, uri))
        }.onFailure {
            statusView.text = "没有可用的浏览器，请手动打开登录地址"
        }
    }

    private fun formatResetAt(epochSeconds: Long?): String {
        if (epochSeconds == null) return "重置时间未知"
        val remaining = epochSeconds - System.currentTimeMillis() / 1_000L
        if (remaining <= 0L) return "已到期或正在刷新"
        return when {
            remaining < 3_600L -> "${ceilDiv(remaining, 60L)} 分钟后重置"
            remaining < 86_400L -> "${ceilDiv(remaining, 3_600L)} 小时后重置"
            else -> "${ceilDiv(remaining, 86_400L)} 天后重置"
        }
    }

    private fun formatTime(epochMillis: Long): String =
        java.text.SimpleDateFormat("HH:mm", java.util.Locale.getDefault()).format(epochMillis)

    private fun ceilDiv(value: Long, divisor: Long): Long = (value + divisor - 1L) / divisor

    private fun textView(text: String, size: Float, style: Int): TextView = TextView(this).apply {
        this.text = text
        textSize = size
        setTypeface(typeface, style)
    }

    private fun button(text: String, action: () -> Unit): Button = Button(this).apply {
        this.text = text
        setOnClickListener { action() }
    }

    private fun marginParams(
        top: Int = 0,
        bottom: Int = 0,
        left: Int = 0,
        right: Int = 0,
    ): LinearLayout.LayoutParams = LinearLayout.LayoutParams(
        LinearLayout.LayoutParams.MATCH_PARENT,
        LinearLayout.LayoutParams.WRAP_CONTENT,
    ).apply {
        setMargins(dp(left), dp(top), dp(right), dp(bottom))
    }

    private fun weightParams(left: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            setMargins(dp(left), 0, 0, 0)
        }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt().coerceAtLeast(value)

    override fun onDestroy() {
        activeProbe?.stop()
        worker.shutdownNow()
        super.onDestroy()
    }
}
