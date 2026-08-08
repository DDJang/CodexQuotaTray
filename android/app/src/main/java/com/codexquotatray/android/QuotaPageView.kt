package com.codexquotatray.android

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Intent
import android.content.IntentFilter
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import com.codexquotatray.android.auth.OAuthStore
import com.codexquotatray.android.quota.CodexQuotaRepository
import com.codexquotatray.android.quota.QuotaReadException
import com.codexquotatray.android.quota.QuotaRefreshEvents
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.ui.QuotaCardModel
import com.codexquotatray.android.ui.QuotaUiModel
import com.codexquotatray.android.ui.QuotaUiStatus
import com.codexquotatray.android.ui.quotaErrorUiModel
import com.codexquotatray.android.ui.quotaLoadingUiModel
import com.codexquotatray.android.ui.toQuotaUiModel
import com.codexquotatray.android.ui.unauthenticatedQuotaUiModel
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

internal fun formatResetRemaining(remainingSeconds: Long): String {
    if (remainingSeconds <= 0L) return "已到期或正在刷新"

    val days = remainingSeconds / 86_400L
    val hours = (remainingSeconds % 86_400L) / 3_600L
    val minutes = (remainingSeconds % 3_600L) / 60L
    return when {
        days > 0L -> "$days 天 $hours 小时 $minutes 分钟后重置"
        hours > 0L -> "$hours 小时 $minutes 分钟后重置"
        minutes > 0L -> "$minutes 分钟后重置"
        else -> "不足 1 分钟后重置"
    }
}

internal class QuotaPageView(private val host: MainActivity) : LinearLayout(host) {
    private val worker = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val repository by lazy { CodexQuotaRepository(host) }
    private val snapshotStore by lazy { QuotaSnapshotStore(host) }
    private val palette by lazy { AppTheme.palette(host) }
    private var busy = false
    private var lastSuccessfulModel: QuotaUiModel? = null
    private var lastKnownAuthenticated: Boolean? = null
    private var refreshReceiverRegistered = false
    private var initialized = false
    private var refreshStateListener: ((enabled: Boolean, busy: Boolean) -> Unit)? = null

    private val refreshReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: android.content.Context?, intent: Intent?) {
            if (intent?.action != QuotaRefreshEvents.ACTION_COMPLETED || busy) return
            renderLatestSnapshot()
        }
    }

    private lateinit var accountView: TextView
    private lateinit var statusView: TextView
    private lateinit var windowsContainer: LinearLayout
    private lateinit var updatedView: TextView
    private lateinit var loginButton: Button

    init {
        orientation = LinearLayout.VERTICAL
        setBackgroundColor(palette.background)
        addView(
            buildContent(),
            LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
    }

    fun initialize() {
        if (initialized) return
        initialized = true
        lastKnownAuthenticated = OAuthStore(host).load() != null
        QuotaRefreshScheduler.schedule(host)
        if (lastKnownAuthenticated == true) {
            val cached = loadLatestModel()
            lastSuccessfulModel = cached
            render(quotaLoadingUiModel(cached))
            refresh()
        } else {
            lastSuccessfulModel = null
            render(unauthenticatedQuotaUiModel())
        }
    }

    fun onStartPage() {
        if (refreshReceiverRegistered) return
        val filter = IntentFilter(QuotaRefreshEvents.ACTION_COMPLETED)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            host.registerReceiver(refreshReceiver, filter, android.content.Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            host.registerReceiver(refreshReceiver, filter)
        }
        refreshReceiverRegistered = true
    }

    fun onStopPage() {
        if (!refreshReceiverRegistered) return
        host.unregisterReceiver(refreshReceiver)
        refreshReceiverRegistered = false
    }

    fun onResumePage() {
        if (!::accountView.isInitialized) return
        val authenticated = OAuthStore(host).load() != null
        if (lastKnownAuthenticated == authenticated) {
            if (authenticated && !busy) renderLatestSnapshot()
            return
        }
        lastKnownAuthenticated = authenticated
        if (!authenticated) {
            lastSuccessfulModel = null
            snapshotStore.clear()
            if (!busy) render(unauthenticatedQuotaUiModel())
        } else if (!busy) {
            lastSuccessfulModel = null
            QuotaRefreshScheduler.schedule(host)
            refresh()
        }
    }

    fun onLoginResult(requestCode: Int, resultCode: Int) {
        if (requestCode == LOGIN_REQUEST_CODE && resultCode == Activity.RESULT_OK) {
            QuotaRefreshScheduler.schedule(host)
        }
    }

    fun onDestroyPage() {
        onStopPage()
        worker.shutdownNow()
    }

    fun setRefreshStateListener(listener: ((enabled: Boolean, busy: Boolean) -> Unit)?) {
        refreshStateListener = listener
        publishRefreshState()
    }

    fun requestRefresh() {
        if (canRequestRefresh()) refresh()
    }

    fun canRequestRefresh(): Boolean = !busy && OAuthStore(host).load() != null

    /** Keeps the action row above the edge-to-edge bottom navigation overlay. */
    fun setBottomSafePadding(bottom: Int) {
        setPadding(paddingLeft, paddingTop, paddingRight, bottom.coerceAtLeast(0))
    }

    private fun buildContent(): View {
        val padding = dp(20)
        val root = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, dp(12), padding, dp(18))
            setBackgroundColor(palette.background)
        }
        val content = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(12), 0, dp(16))
        }
        val scroll = ScrollView(host).apply {
            isFillViewport = true
            addView(content)
        }

        accountView = textView("Codex / 当前账户", 16f, Typeface.NORMAL).apply {
            setTextColor(palette.secondary)
        }
        content.addView(accountView, marginParams(top = 4, bottom = 24))
        statusView = textView("正在读取额度…", 16f, Typeface.NORMAL).apply {
            setTextColor(palette.secondary)
        }
        content.addView(statusView, marginParams(bottom = 20))
        windowsContainer = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
        content.addView(windowsContainer)
        updatedView = textView("尚未更新", 13f, Typeface.NORMAL).apply {
            setTextColor(palette.muted)
        }
        content.addView(updatedView, marginParams(top = 8, bottom = 8))

        root.addView(
            scroll,
            LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f,
            ),
        )

        loginButton = button("登录 Codex", primary = false) { openLogin() }
        root.addView(loginButton, marginParams(top = 8))
        return root
    }

    private fun refresh() {
        if (busy) return
        busy = true
        publishRefreshState()
        val previous = lastSuccessfulModel
        render(quotaLoadingUiModel(previous))
        worker.execute {
            val result = runCatching { repository.refresh() }
            mainHandler.post {
                busy = false
                val model = result.fold(
                    onSuccess = { value ->
                        val candidate = value.toQuotaUiModel()
                        if (candidate.status == QuotaUiStatus.LOADED) {
                            lastSuccessfulModel = candidate
                            candidate
                        } else {
                            AppLogStore.record(host, "额度详情暂不可用", "WARN")
                            quotaErrorUiModel(
                                candidate.message ?: "额度详情暂不可用",
                                previous = previous,
                            )
                        }
                    },
                    onFailure = { error ->
                        AppLogStore.record(
                            host,
                            "额度读取失败：${error.message ?: "未知错误"}",
                            "WARN",
                        )
                        if (error is QuotaReadException &&
                            error.kind == com.codexquotatray.android.quota.QuotaReadFailureKind.LOGIN_REQUIRED
                        ) {
                            lastKnownAuthenticated = false
                            lastSuccessfulModel = null
                            snapshotStore.clear()
                            QuotaRefreshScheduler.cancel(host)
                        }
                        modelForFailure(error, previous)
                    },
                )
                render(model)
                publishRefreshState()
            }
        }
    }

    private fun loadLatestModel(): QuotaUiModel? = snapshotStore.load()
        ?.takeIf { it.quotaState != "unavailable" }
        ?.toQuotaUiModel()
        ?.takeIf { it.status == QuotaUiStatus.LOADED }

    private fun renderLatestSnapshot() {
        val latest = loadLatestModel() ?: return
        val latestTime = latest.updatedAtMillis ?: return
        val currentTime = lastSuccessfulModel?.updatedAtMillis ?: Long.MIN_VALUE
        if (latestTime < currentTime) return
        lastSuccessfulModel = latest
        render(latest)
    }

    private fun openLogin() {
        host.startActivityForResult(
            Intent(host, LoginActivity::class.java),
            LOGIN_REQUEST_CODE,
        )
    }

    private fun modelForFailure(
        error: Throwable,
        previous: QuotaUiModel? = lastSuccessfulModel,
    ): QuotaUiModel = when (error) {
        is QuotaReadException -> when (error.kind) {
            com.codexquotatray.android.quota.QuotaReadFailureKind.LOGIN_REQUIRED ->
                unauthenticatedQuotaUiModel()

            else -> quotaErrorUiModel(error.message, previous)
        }

        else -> quotaErrorUiModel("额度读取失败", previous)
    }

    private fun render(model: QuotaUiModel) {
        accountView.text = "${model.accountLabel} / 当前账户"
        accountView.visibility = if (model.status == QuotaUiStatus.UNAUTHENTICATED) {
            GONE
        } else {
            VISIBLE
        }
        val unauthenticated = model.status == QuotaUiStatus.UNAUTHENTICATED
        statusView.text = model.message ?: when (model.status) {
            QuotaUiStatus.LOADING -> "正在读取额度…"
            QuotaUiStatus.UNAUTHENTICATED -> "尚未登录 Codex"
            QuotaUiStatus.LOADED -> "额度读取成功"
            QuotaUiStatus.ERROR -> "额度读取失败"
        }
        statusView.setTextColor(
            if (model.status == QuotaUiStatus.ERROR) palette.error else palette.body,
        )
        windowsContainer.removeAllViews()
        windowsContainer.visibility = if (unauthenticated) GONE else VISIBLE
        updatedView.visibility = if (unauthenticated) GONE else VISIBLE
        if (model.status == QuotaUiStatus.LOADED && model.windows.isEmpty()) {
            windowsContainer.addView(textView("当前没有可用额度窗口", 15f, Typeface.NORMAL))
        } else {
            model.windows.forEach { windowsContainer.addView(windowCard(it)) }
        }
        updatedView.text = model.updatedAtMillis?.let { "更新于 ${formatTime(it)}" } ?: "尚未更新"
        loginButton.visibility = if (unauthenticated) VISIBLE else GONE
        loginButton.isEnabled = !busy
        publishRefreshState()
    }

    private fun publishRefreshState() {
        refreshStateListener?.invoke(canRequestRefresh(), busy)
    }

    private fun windowCard(window: QuotaCardModel): View = LinearLayout(host).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(16), dp(16), dp(16), dp(16))
        background = GradientDrawable().apply {
            setColor(palette.surface)
            setStroke(dp(1), palette.border)
            cornerRadius = dp(16).toFloat()
        }
        elevation = dp(2).toFloat()
        addView(textView(window.title, 17f, Typeface.BOLD).apply {
            setTextColor(palette.body)
        })
        val remaining = window.remainingPercent
        addView(
            textView(
                remaining?.let { "剩余 $it%" } ?: "剩余未知",
                26f,
                Typeface.BOLD,
            ).apply {
                setTextColor(palette.accent)
            },
            marginParams(top = 10),
        )
        if (remaining != null) {
            addView(ProgressBar(host, null, android.R.attr.progressBarStyleHorizontal).apply {
                max = 100
                progress = remaining.coerceIn(0, 100)
                progressTintList = ColorStateList.valueOf(palette.accent)
                progressBackgroundTintList = ColorStateList.valueOf(palette.progressTrack)
                minimumHeight = dp(8)
                contentDescription = "剩余 $remaining%"
            }, marginParams(top = 10))
        }
        addView(
            textView(formatResetAt(window.resetsAt), 14f, Typeface.NORMAL).apply {
                setTextColor(palette.secondary)
                setLineSpacing(dp(3).toFloat(), 1.0f)
            },
            marginParams(top = 12),
        )
    }.also {
        it.layoutParams = marginParams(bottom = 14)
    }

    private fun formatResetAt(epochSeconds: Long?): String {
        if (epochSeconds == null) return "重置时间未知"
        val absolute = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.getDefault())
            .format(Date(epochSeconds * 1_000L))
        val remaining = epochSeconds - System.currentTimeMillis() / 1_000L
        val relative = formatResetRemaining(remaining)
        return "重置于 $absolute\n$relative"
    }

    private fun formatTime(epochMillis: Long): String =
        SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date(epochMillis))

    private fun textView(text: String, size: Float, style: Int): TextView = TextView(host).apply {
        this.text = text
        textSize = size
        setTypeface(typeface, style)
        setTextColor(palette.body)
    }

    private fun button(text: String, primary: Boolean, action: () -> Unit): Button = Button(host).apply {
        this.text = text
        textSize = 15f
        isAllCaps = false
        setTypeface(typeface, Typeface.BOLD)
        minimumHeight = dp(52)
        minimumHeight = dp(52)
        backgroundTintList = ColorStateList.valueOf(
            if (primary) palette.primaryButton else palette.secondaryButton,
        )
        setTextColor(if (primary) palette.onPrimary else palette.secondaryButtonText)
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

    companion object {
        private const val LOGIN_REQUEST_CODE = 1003
    }
}
