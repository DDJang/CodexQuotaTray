package com.codexquotatray.android

import android.app.Activity
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.widget.Button
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.codexquotatray.android.usage.HeatmapBuckets
import com.codexquotatray.android.usage.TokenFormatter
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenSyncEndpoint
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

class TokenUsageActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }
    private val cache by lazy { TokenUsageCache(this) }
    private val pairingStore by lazy { TokenSyncStore(this) }
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private lateinit var status: TextView
    private lateinit var summary: LinearLayout
    private lateinit var selectedDay: TextView
    private lateinit var heatmapHost: LinearLayout
    private lateinit var heatmapScroll: HorizontalScrollView
    private lateinit var syncButton: Button
    private var lastSnapshot: TokenUsageSnapshot? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContentView(buildContent())
        cache.load()?.let {
            lastSnapshot = it
            render(it, "上次同步于 ${formatSyncTime(it.generatedAtUtc)}")
        } ?: run { status.text = "暂无 Token 使用量缓存" }
        sync()
    }

    override fun onDestroy() {
        worker.shutdownNow()
        super.onDestroy()
    }

    private fun buildContent(): View {
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(22), dp(20), dp(24))
            setBackgroundColor(palette.background)
        }
        val toolbar = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        toolbar.addView(text("‹", 34f).apply { setOnClickListener { finish() }; gravity = Gravity.CENTER }, LinearLayout.LayoutParams(dp(48), dp(48)))
        toolbar.addView(text("使用统计", 24f, true).apply { setTextColor(palette.title) }, LinearLayout.LayoutParams(0, -2, 1f))
        content.addView(toolbar)
        status = text("", 14f).apply { setTextColor(palette.muted) }
        content.addView(status, margins(bottom = 12))
        summary = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        content.addView(summary)
        content.addView(text("Token 活动", 18f, true).apply { setTextColor(palette.title) }, margins(top = 22, bottom = 8))
        selectedDay = text("触摸方格查看当日用量", 14f).apply { setTextColor(palette.secondary) }
        content.addView(selectedDay, margins(bottom = 8))
        heatmapHost = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        heatmapScroll = HorizontalScrollView(this).apply { isHorizontalScrollBarEnabled = true; addView(heatmapHost) }
        content.addView(heatmapScroll, margins(bottom = 16))
        syncButton = Button(this).apply { text = "同步"; setOnClickListener { sync() } }
        content.addView(syncButton)
        return ScrollView(this).apply { isFillViewport = true; addView(content) }
    }

    private fun sync() {
        val pairing = pairingStore.load()
        if (pairing == null) {
            status.text = if (lastSnapshot == null) "请先在设置中扫码或输入 Windows 配对信息" else "未配置 Windows 配对；正在显示缓存"
            return
        }
        syncButton.isEnabled = false
        status.text = if (lastSnapshot == null) "正在从 Windows 同步…" else "正在同步；当前显示缓存"
        worker.execute {
            runCatching { TokenUsageSyncClient(this).sync(pairing) }
                .onSuccess { result ->
                    val snapshot = result.snapshot
                    pairingStore.save(TokenSyncEndpoint.markSynced(result.pairing, snapshot))
                    cache.save(snapshot)
                    main.post {
                        lastSnapshot = snapshot
                        syncButton.isEnabled = true
                        render(snapshot, "上次同步于 ${formatSyncTime(snapshot.generatedAtUtc)}")
                    }
                }
                .onFailure { error -> main.post {
                    syncButton.isEnabled = true
                    val message = (error as? TokenUsageException)?.message ?: "Windows 当前不可用"
                    status.text = if (lastSnapshot == null) message else "上次同步于 ${formatSyncTime(lastSnapshot!!.generatedAtUtc)} · $message"
                } }
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

    private fun summaryRow(items: List<Pair<String, Long>>): View = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        items.forEach { (label, value) ->
            addView(LinearLayout(this@TokenUsageActivity).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(8), dp(12), dp(8), dp(12))
                addView(text(TokenFormatter.format(value), 19f, true).apply { setTextColor(palette.title); gravity = Gravity.CENTER })
                addView(text(label, 12f).apply { setTextColor(palette.muted); gravity = Gravity.CENTER })
            }, LinearLayout.LayoutParams(0, -2, 1f))
        }
    }

    private inner class TokenHeatmapView(days: List<TokenUsageDay>, private val selected: (TokenUsageDay) -> Unit) : View(this) {
        private val cell = dp(15).toFloat()
        private val gap = dp(3).toFloat()
        private val start = LocalDate.now().minusDays(364)
        private val values = days.associateBy { it.date }
        private val nonZero = days.map { it.totalTokens }.filter { it > 0L }
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val bucketColors = intArrayOf(palette.progressTrack, 0xffc6e48b.toInt(), 0xff7bc96f.toInt(), 0xff239a3b.toInt(), 0xff196127.toInt())

        init {
            layoutParams = LinearLayout.LayoutParams(((cell + gap) * 53).toInt(), ((cell + gap) * 7).toInt())
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
                if (index in 0 until 365) selected(values[start.plusDays(index.toLong())] ?: TokenUsageDay(start.plusDays(index.toLong()), 0, null, null, null, null))
                performClick()
            }
            return true
        }

        override fun performClick(): Boolean = super.performClick()
    }

    private fun formatSyncTime(raw: String): String = runCatching {
        SimpleDateFormat("HH:mm", Locale.getDefault()).format(Date.from(Instant.parse(raw)))
    }.getOrDefault("未知")

    private fun text(value: String, size: Float, bold: Boolean = false) = TextView(this).apply {
        text = value; textSize = size; setTextColor(palette.body); if (bold) setTypeface(typeface, android.graphics.Typeface.BOLD)
    }

    private fun margins(top: Int = 0, bottom: Int = 0) = LinearLayout.LayoutParams(-1, -2).apply {
        topMargin = dp(top); bottomMargin = dp(bottom)
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()
}
