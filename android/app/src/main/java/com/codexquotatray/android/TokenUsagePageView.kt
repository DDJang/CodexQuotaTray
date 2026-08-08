package com.codexquotatray.android

import android.content.Intent
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.widget.Button
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.codexquotatray.android.usage.HeatmapBuckets
import com.codexquotatray.android.usage.TokenFormatter
import com.codexquotatray.android.usage.TokenSyncEndpoint
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenUsageCache
import com.codexquotatray.android.usage.TokenUsageDay
import com.codexquotatray.android.usage.TokenUsageException
import com.codexquotatray.android.usage.TokenUsageSnapshot
import com.codexquotatray.android.usage.TokenUsageSyncClient
import java.text.SimpleDateFormat
import java.time.Instant
import java.time.LocalDate
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

internal class TokenUsagePageView(private val host: MainActivity) : FrameLayout(host) {
    private val palette by lazy { AppTheme.palette(host) }
    private val cache by lazy { TokenUsageCache(host) }
    private val pairingStore by lazy { TokenSyncStore(host) }
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var pairingKey: String? = null
    private var pairingStateLoaded = false
    private var syncing = false
    private var visible = false
    private var destroyed = false
    private var lastSnapshot: TokenUsageSnapshot? = null

    private lateinit var status: TextView
    private lateinit var emptyState: LinearLayout
    private lateinit var dataContainer: LinearLayout
    private lateinit var summary: LinearLayout
    private lateinit var selectedDay: TextView
    private lateinit var heatmapHost: LinearLayout
    private lateinit var heatmapScroll: HorizontalScrollView
    private lateinit var syncButton: Button

    init {
        setBackgroundColor(palette.background)
        addView(
            buildContent(),
            FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
    }

    fun onVisible() {
        visible = true
        val pairing = pairingStore.load()
        val nextKey = pairing?.let { "${it.deviceId}|${it.pairingSecret}" }
        if (pairingStateLoaded && nextKey == pairingKey) return
        pairingStateLoaded = true
        pairingKey = nextKey
        if (tokenUsagePageMode(pairing != null) == TokenUsagePageMode.EMPTY_UNPAIRED) {
            renderEmptyState()
            return
        }

        renderPairedState()
        cache.load()?.let {
            lastSnapshot = it
            render(it, "上次同步于 ${formatSyncTime(it.generatedAtUtc)}")
        } ?: run { status.text = "暂无 Token 使用量缓存" }
        sync(pairing)
    }

    fun onResumed() {
        if (visible) onVisible()
    }

    fun onHidden() {
        visible = false
    }

    fun onDestroyPage() {
        destroyed = true
        worker.shutdownNow()
    }

    private fun buildContent(): View {
        val content = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(12), dp(20), dp(24))
            setBackgroundColor(palette.background)
        }
        content.addView(
            text("Token 使用统计", 21f, true).apply { setTextColor(palette.title) },
            margins(bottom = 8),
        )
        status = text("尚未打开统计", 14f).apply { setTextColor(palette.muted) }
        content.addView(status, margins(bottom = 14))

        emptyState = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(20), dp(26), dp(20), dp(26))
            background = cardBackground()
            addView(text("Token 使用统计", 19f, true).apply { setTextColor(palette.title) })
            addView(
                text(
                    "连接 Windows CodexQuotaTray 后，\n即可查看本机 Codex Token 使用历史。",
                    14f,
                ).apply {
                    setTextColor(palette.secondary)
                    gravity = Gravity.CENTER
                },
                margins(top = 10, bottom = 16),
            )
            addView(actionButton("前往设置") {
                host.startActivity(Intent(host, SettingsActivity::class.java))
            })
        }
        content.addView(emptyState, margins(bottom = 12))

        dataContainer = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
        summary = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
        dataContainer.addView(summary)
        dataContainer.addView(
            text("Token 活动", 18f, true).apply { setTextColor(palette.title) },
            margins(top = 22, bottom = 8),
        )
        selectedDay = text("触摸方格查看当日用量", 14f).apply { setTextColor(palette.secondary) }
        dataContainer.addView(selectedDay, margins(bottom = 8))
        heatmapHost = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
        heatmapScroll = HorizontalScrollView(host).apply {
            isHorizontalScrollBarEnabled = true
            addView(heatmapHost)
        }
        dataContainer.addView(heatmapScroll, margins(bottom = 16))
        syncButton = actionButton("同步") { sync(pairingStore.load()) }
        dataContainer.addView(syncButton)
        content.addView(dataContainer)

        return ScrollView(host).apply {
            isFillViewport = true
            addView(content)
        }
    }

    private fun renderEmptyState() {
        emptyState.visibility = VISIBLE
        dataContainer.visibility = GONE
        status.text = "尚未配对 Windows"
    }

    private fun renderPairedState() {
        emptyState.visibility = GONE
        dataContainer.visibility = VISIBLE
    }

    private fun sync(pairing: com.codexquotatray.android.usage.TokenSyncPairing?) {
        if (pairing == null || syncing || destroyed) return
        syncing = true
        syncButton.isEnabled = false
        status.text = if (lastSnapshot == null) "正在从 Windows 同步…" else "正在同步；当前显示缓存"
        worker.execute {
            val result = runCatching { TokenUsageSyncClient(host).sync(pairing) }
            main.post {
                if (destroyed) return@post
                syncing = false
                syncButton.isEnabled = true
                result.onSuccess { synced ->
                    val snapshot = synced.snapshot
                    pairingStore.save(TokenSyncEndpoint.markSynced(synced.pairing, snapshot))
                    cache.save(snapshot)
                    lastSnapshot = snapshot
                    render(snapshot, "上次同步于 ${formatSyncTime(snapshot.generatedAtUtc)}")
                }.onFailure { error ->
                    val message = (error as? TokenUsageException)?.message ?: "Windows 当前不可用"
                    status.text = if (lastSnapshot == null) message else
                        "上次同步于 ${formatSyncTime(lastSnapshot!!.generatedAtUtc)} · $message"
                }
            }
        }
    }

    private fun render(snapshot: TokenUsageSnapshot, statusText: String) {
        status.text = statusText
        summary.removeAllViews()
        val first = listOf(
            "今日 Token" to snapshot.summary.todayTokens,
            "7 天 Token" to snapshot.summary.last7DaysTokens,
            "30 天 Token" to snapshot.summary.last30DaysTokens,
            "累计 Token" to snapshot.summary.lifetimeTokens,
        )
        val second = listOf(
            "峰值 Token" to snapshot.summary.peakDailyTokens,
            "当前连续天数" to snapshot.summary.currentStreak.toLong(),
            "最长连续天数" to snapshot.summary.longestStreak.toLong(),
        )
        summary.addView(summaryRow(first))
        summary.addView(summaryRow(second), margins(top = 8))
        heatmapHost.removeAllViews()
        heatmapHost.addView(TokenHeatmapView(snapshot.days) { day ->
            selectedDay.text = "${day.date.monthValue} 月 ${day.date.dayOfMonth} 日  ${String.format(Locale.US, "%,d", day.totalTokens)} Token"
        })
        heatmapScroll.post { heatmapScroll.fullScroll(View.FOCUS_RIGHT) }
    }

    private fun summaryRow(items: List<Pair<String, Long>>): View = LinearLayout(host).apply {
        orientation = LinearLayout.HORIZONTAL
        items.forEach { (label, value) ->
            addView(LinearLayout(host).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(8), dp(12), dp(8), dp(12))
                addView(text(TokenFormatter.format(value), 19f, true).apply {
                    setTextColor(palette.title)
                    gravity = Gravity.CENTER
                })
                addView(text(label, 12f).apply {
                    setTextColor(palette.muted)
                    gravity = Gravity.CENTER
                })
            }, LinearLayout.LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
        }
    }

    private inner class TokenHeatmapView(
        days: List<TokenUsageDay>,
        private val selected: (TokenUsageDay) -> Unit,
    ) : View(host) {
        private val cell = dp(15).toFloat()
        private val gap = dp(3).toFloat()
        private val start = LocalDate.now().minusDays(364)
        private val values = days.associateBy { it.date }
        private val nonZero = days.map { it.totalTokens }.filter { it > 0L }
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val bucketColors = intArrayOf(
            palette.progressTrack,
            0xffc6e48b.toInt(),
            0xff7bc96f.toInt(),
            0xff239a3b.toInt(),
            0xff196127.toInt(),
        )

        init {
            layoutParams = LinearLayout.LayoutParams(
                ((cell + gap) * 53).toInt(),
                ((cell + gap) * 7).toInt(),
            )
        }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            for (index in 0 until 365) {
                val date = start.plusDays(index.toLong())
                val tokens = values[date]?.totalTokens ?: 0L
                paint.color = bucketColors[HeatmapBuckets.bucket(tokens, nonZero)]
                val x = (index / 7) * (cell + gap)
                val y = (index % 7) * (cell + gap)
                canvas.drawRoundRect(RectF(x, y, x + cell, y + cell), dp(2).toFloat(), dp(2).toFloat(), paint)
            }
        }

        override fun onTouchEvent(event: MotionEvent): Boolean {
            if (event.action == MotionEvent.ACTION_UP) {
                val column = (event.x / (cell + gap)).toInt()
                val row = (event.y / (cell + gap)).toInt()
                val index = column * 7 + row
                if (index in 0 until 365) {
                    selected(values[start.plusDays(index.toLong())] ?: TokenUsageDay(
                        start.plusDays(index.toLong()),
                        0,
                        null,
                        null,
                        null,
                        null,
                    ))
                }
                performClick()
            }
            return true
        }

        override fun performClick(): Boolean = super.performClick()
    }

    private fun formatSyncTime(raw: String): String = runCatching {
        SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw)))
    }.getOrDefault("未知")

    private fun text(value: String, size: Float, bold: Boolean = false): TextView = TextView(host).apply {
        this.text = value
        textSize = size
        setTextColor(palette.body)
        if (bold) setTypeface(typeface, Typeface.BOLD)
    }

    private fun actionButton(value: String, action: () -> Unit): Button = Button(host).apply {
        text = value
        textSize = 14f
        isAllCaps = false
        setTypeface(typeface, Typeface.BOLD)
        minimumHeight = dp(48)
        backgroundTintList = android.content.res.ColorStateList.valueOf(palette.secondaryButton)
        setTextColor(palette.secondaryButtonText)
        setOnClickListener { action() }
    }

    private fun cardBackground(): GradientDrawable = GradientDrawable().apply {
        setColor(palette.surface)
        setStroke(dp(1), palette.border)
        cornerRadius = dp(14).toFloat()
    }

    private fun margins(top: Int = 0, bottom: Int = 0) = LinearLayout.LayoutParams(-1, -2).apply {
        topMargin = dp(top)
        bottomMargin = dp(bottom)
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
}
