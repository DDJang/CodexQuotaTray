package com.codexquotatray.android

import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Typeface
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class LogActivity : Activity() {
    private val mainHandler = Handler(Looper.getMainLooper())
    private val palette by lazy { AppTheme.palette(this) }
    private val logStore by lazy { AppLogStore(this) }
    private lateinit var logView: TextView
    private lateinit var copyButton: Button
    private lateinit var clearButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContentView(buildContent())
        renderLog()
    }

    override fun onResume() {
        super.onResume()
        if (::logView.isInitialized) renderLog()
    }

    private fun buildContent(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(22), dp(20), dp(18))
            setBackgroundColor(palette.background)
        }
        val toolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        toolbar.addView(backButton())
        toolbar.addView(
            textView("日志", 24f, Typeface.BOLD).apply { setTextColor(palette.title) },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
        )
        root.addView(toolbar, marginParams(bottom = 12))

        root.addView(
            textView("这里只显示脱敏后的本地运行摘要，不包含 token、设备码或完整响应。", 13f, Typeface.NORMAL).apply {
                setTextColor(palette.muted)
            },
            marginParams(bottom = 12),
        )

        logView = textView("暂无日志", 13f, Typeface.NORMAL).apply {
            typeface = Typeface.MONOSPACE
           setTextColor(palette.body)
           setPadding(dp(14), dp(14), dp(14), dp(14))
           setBackgroundColor(palette.surface)
           setLineSpacing(dp(3).toFloat(), 1.0f)
            setTextIsSelectable(true)
       }
        val scroll = ScrollView(this).apply {
            isFillViewport = true
            addView(logView)
        }
        root.addView(
            scroll,
            LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                0,
                1f,
            ).apply { setMargins(0, 0, 0, dp(12)) },
        )

        copyButton = actionButton("复制日志") { copyLogs() }
        clearButton = actionButton("清空日志") { confirmClearLogs() }
        val actions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            addView(copyButton, weightParams())
            addView(clearButton, weightParams(left = 8))
        }
        root.addView(actions)
        return root
    }

    private fun renderLog() {
        logView.text = logStore.read()
    }

    private fun copyLogs() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboard?.setPrimaryClip(ClipData.newPlainText("CodexQuota 日志", logStore.read()))
        copyButton.isEnabled = false
        copyButton.text = "已复制"
        mainHandler.postDelayed({
            if (!isFinishing && !isDestroyed) {
                copyButton.isEnabled = true
                copyButton.text = "复制日志"
            }
        }, COPY_FEEDBACK_MILLIS)
    }

    private fun confirmClearLogs() {
        AlertDialog.Builder(this)
            .setTitle("清空日志")
            .setMessage("确定清空全部本地日志吗？")
            .setNegativeButton("取消", null)
            .setPositiveButton("清空") { _, _ ->
                logStore.clear()
                renderLog()
            }
            .show()
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
        ).apply { setMargins(0, 0, 0, dp(bottom)) }

    private fun weightParams(left: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply {
            setMargins(dp(left), 0, 0, 0)
        }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt().coerceAtLeast(value)

    companion object {
        private const val COPY_FEEDBACK_MILLIS = 1_200L
    }
}
