package com.codexquotatray.android

import android.app.Activity
import android.content.Intent
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class AboutActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        setContentView(buildContent())
    }

    private fun buildContent(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(22), dp(20), dp(18))
            setBackgroundColor(palette.background)
        }
        val scroll = ScrollView(this).apply { isFillViewport = true }
        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
        }
        scroll.addView(content)

        val toolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        toolbar.addView(backButton())
        toolbar.addView(
            textView("关于", 24f, Typeface.BOLD).apply {
                setTextColor(palette.title)
            },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
        )
        content.addView(toolbar, marginParams(bottom = 8))

        val aboutBody = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
        }
        val bodyParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            0,
            1f,
        )
        content.addView(aboutBody, bodyParams)
        aboutBody.addView(
            ImageView(this).apply {
                setImageResource(R.drawable.ic_launcher_mark)
                contentDescription = "CodexQuota 图标"
                scaleType = ImageView.ScaleType.CENTER_INSIDE
            },
            LinearLayout.LayoutParams(dp(112), dp(112)).apply {
                gravity = Gravity.CENTER_HORIZONTAL
                setMargins(0, dp(120), 0, dp(24))
            },
        )
        aboutBody.addView(
            textView("CodexQuota", 22f, Typeface.BOLD).apply {
                setTextColor(palette.title)
            },
            marginParams(bottom = 8),
        )
        aboutBody.addView(
            textView("版本 ${installedVersion()}", 14f, Typeface.NORMAL).apply {
                setTextColor(palette.muted)
            },
            marginParams(bottom = 18),
        )
        aboutBody.addView(
            textView(PROJECT_URL, 14f, Typeface.NORMAL).apply {
                setTextColor(palette.accent)
                isClickable = true
                setOnClickListener { openProjectPage() }
            },
            marginParams(bottom = 20),
        )
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

    @Suppress("DEPRECATION")
    private fun installedVersion(): String =
        packageManager.getPackageInfo(packageName, 0).versionName ?: "未知"

    private fun openProjectPage() {
        runCatching {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(PROJECT_URL)))
        }
    }

    private fun backButton(): TextView = textView("‹", 34f, Typeface.NORMAL).apply {
        gravity = Gravity.CENTER
        setTextColor(palette.secondaryButtonText)
        isClickable = true
        setOnClickListener { finish() }
        layoutParams = LinearLayout.LayoutParams(dp(48), dp(48))
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

    companion object {
        private const val PROJECT_URL = "https://github.com/DDJang/CodexQuotaTray"
    }
}
