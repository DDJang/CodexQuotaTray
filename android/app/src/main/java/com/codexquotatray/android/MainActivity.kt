package com.codexquotatray.android

import android.app.Activity
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.view.WindowInsetsCompat

class MainActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }
    private var appliedTheme: ThemeMode? = null
    private var tabState = MainTabState()
    private lateinit var quotaPage: QuotaPageView
    private lateinit var tokenUsagePage: TokenUsagePageView
    private lateinit var pageContainer: FrameLayout
    private lateinit var bottomBar: LiquidGlassBottomBar

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        appliedTheme = AppTheme.effectiveMode(this)
        AppLogStore.record(this, "应用启动")

        setContentView(buildContent())
        quotaPage.initialize()
        selectTab(MainTab.QUOTA)
    }

    override fun onStart() {
        super.onStart()
        if (::quotaPage.isInitialized) quotaPage.onStartPage()
    }

    override fun onStop() {
        if (::quotaPage.isInitialized) quotaPage.onStopPage()
        super.onStop()
    }

    override fun onResume() {
        super.onResume()
        if (appliedTheme != AppTheme.effectiveMode(this)) {
            recreate()
            return
        }
        if (::quotaPage.isInitialized) quotaPage.onResumePage()
        if (tabState.selectedTab == MainTab.USAGE && ::tokenUsagePage.isInitialized) {
            tokenUsagePage.onResumed()
        }
    }

    @Suppress("DEPRECATION")
    override fun onBackPressed() {
        tabState.backToQuota()?.let {
            selectTab(MainTab.QUOTA)
            return
        }
        super.onBackPressed()
    }

    @Suppress("DEPRECATION")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        quotaPage.onLoginResult(requestCode, resultCode)
    }

    override fun onDestroy() {
        if (::quotaPage.isInitialized) quotaPage.onDestroyPage()
        if (::tokenUsagePage.isInitialized) tokenUsagePage.onDestroyPage()
        super.onDestroy()
    }

    private fun buildContent(): View {
        val root = FrameLayout(this).apply {
            setBackgroundColor(palette.background)
            clipChildren = false
        }

        val content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(palette.background)
            setPadding(0, 0, 0, dp(104))
        }
        content.addView(buildHeader(), LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT,
        ))

        pageContainer = FrameLayout(this).apply {
            setBackgroundColor(palette.background)
        }
        quotaPage = QuotaPageView(this)
        tokenUsagePage = TokenUsagePageView(this)
        pageContainer.addView(quotaPage, pageLayoutParams())
        pageContainer.addView(tokenUsagePage, pageLayoutParams())
        content.addView(pageContainer, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            0,
            1f,
        ))

        root.addView(content, FrameLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.MATCH_PARENT,
        ))
        bottomBar = LiquidGlassBottomBar(this, content, palette) { tab -> selectTab(tab) }
        root.addView(bottomBar, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            dp(84),
            Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
        ).apply { bottomMargin = dp(10) })
        root.setOnApplyWindowInsetsListener { _, insets ->
            val navBottom = WindowInsetsCompat.toWindowInsetsCompat(insets)
                .getInsets(WindowInsetsCompat.Type.navigationBars()).bottom
            content.setPadding(0, 0, 0, dp(104) + navBottom)
            (bottomBar.layoutParams as? FrameLayout.LayoutParams)?.let { params ->
                params.bottomMargin = dp(10) + navBottom
                bottomBar.layoutParams = params
            }
            insets
        }
        return root
    }

    private fun buildHeader(): View = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(20), dp(14), dp(20), dp(10))
        setBackgroundColor(palette.background)
        addView(
            textView("CodexQuota", 28f, Typeface.BOLD).apply {
                setTextColor(palette.title)
            },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
        )
        addView(settingsButton())
    }

    private fun selectTab(tab: MainTab) {
        val previousTab = tabState.selectedTab
        tabState = tabState.select(tab)
        quotaPage.visibility = if (tab == MainTab.QUOTA) View.VISIBLE else View.GONE
        tokenUsagePage.visibility = if (tab == MainTab.USAGE) View.VISIBLE else View.GONE
        if (tab == MainTab.USAGE) {
            tokenUsagePage.onVisible()
        } else {
            tokenUsagePage.onHidden()
        }
        bottomBar.setSelectedTab(tab, animate = previousTab != tab)
    }

    private fun settingsButton(): ImageButton = ImageButton(this).apply {
        contentDescription = "设置"
        setImageResource(R.drawable.ic_settings)
        imageTintList = ColorStateList.valueOf(palette.secondaryButtonText)
        setPadding(dp(11), dp(11), dp(11), dp(11))
        background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(palette.surface)
            setStroke(dp(1), palette.border)
        }
        setOnClickListener {
            startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
        }
        layoutParams = LinearLayout.LayoutParams(dp(48), dp(48))
    }

    private fun pageLayoutParams(): FrameLayout.LayoutParams = FrameLayout.LayoutParams(
        FrameLayout.LayoutParams.MATCH_PARENT,
        FrameLayout.LayoutParams.MATCH_PARENT,
    )

    private fun textView(text: String, size: Float, style: Int): TextView = TextView(this).apply {
        this.text = text
        textSize = size
        setTypeface(typeface, style)
    }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt().coerceAtLeast(value)
}
