package com.codexquotatray.android

import android.app.Activity
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.graphics.Typeface
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.view.ViewCompat

class MainActivity : Activity() {
    private val palette by lazy { AppTheme.palette(this) }
    private var appliedTheme: ThemeMode? = null
    private var tabState = MainTabState()
    private lateinit var quotaPage: QuotaPageView
    private lateinit var tokenUsagePage: TokenUsagePageView
    private lateinit var pageContainer: FrameLayout
    private lateinit var bottomBar: LiquidGlassBottomBar
    private lateinit var headerView: LinearLayout
    private var quotaActionEnabled = false
    private var quotaActionBusy = false
    private var usageActionEnabled = false
    private var usageActionBusy = false

    override fun onCreate(savedInstanceState: Bundle?) {
        AppTheme.prepare(this)
        super.onCreate(savedInstanceState)
        AppTheme.applySystemBars(this)
        appliedTheme = AppTheme.effectiveMode(this)
        AppLogStore.record(this, "应用启动")

        val root = buildContent()
        setContentView(root)
        // The root must be attached before asking Android to dispatch insets.
        ViewCompat.requestApplyInsets(root)
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
        }
        headerView = buildHeader(content)
        content.addView(headerView, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            LinearLayout.LayoutParams.WRAP_CONTENT,
        ))

        pageContainer = FrameLayout(this).apply {
            setBackgroundColor(palette.background)
        }
        quotaPage = QuotaPageView(this)
        tokenUsagePage = TokenUsagePageView(this)
        quotaPage.setRefreshStateListener { enabled, busy ->
            quotaActionEnabled = enabled
            quotaActionBusy = busy
            if (::bottomBar.isInitialized) updateBottomAction()
        }
        tokenUsagePage.setSyncStateListener { enabled, busy ->
            usageActionEnabled = enabled
            usageActionBusy = busy
            if (::bottomBar.isInitialized) updateBottomAction()
        }
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
        bottomBar = LiquidGlassBottomBar(
            context = this,
            backdropHost = content,
            palette = palette,
            onTabSelected = { tab -> selectTab(tab) },
            onActionClick = { dispatchBottomAction() },
        )
        root.addView(bottomBar, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT,
            bottomBar.requiredHeight(0),
            Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL,
        ))
        ViewCompat.setOnApplyWindowInsetsListener(root) { _, insets ->
            val safeTop = AppTheme.safeTopInset(insets)
            val safeBottom = AppTheme.safeBottomInset(insets)
            if ((applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0) {
                Log.d("CodexQuotaInsets", "safeTop=$safeTop safeBottom=$safeBottom")
            }
            headerView.setPadding(
                headerView.paddingLeft,
                headerBasePaddingTop + safeTop,
                headerView.paddingRight,
                headerBasePaddingBottom,
            )
            bottomBar.setSafeBottomInset(safeBottom)
            val pageBottom = bottomBar.requiredHeight(safeBottom)
            quotaPage.setBottomSafePadding(pageBottom)
            tokenUsagePage.setBottomSafePadding(pageBottom)
            insets
        }
        return root
    }

    // Activity resources are not available during constructor/property
    // initialization. Resolve these only after the Activity is attached.
    private val headerBasePaddingTop by lazy { dp(14) }
    private val headerBasePaddingBottom by lazy { dp(10) }

    private fun buildHeader(backdropHost: ViewGroup): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(20), headerBasePaddingTop, dp(20), headerBasePaddingBottom)
        setBackgroundColor(palette.background)
        addView(
            textView("CodexQuota", 28f, Typeface.BOLD).apply {
                setTextColor(palette.title)
            },
            LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f),
        )
        addView(settingsButton(backdropHost))
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
        updateBottomAction()
    }

    private fun settingsButton(backdropHost: ViewGroup): GlassIconButton = GlassIconButton(
        context = this,
        palette = palette,
        iconRes = R.drawable.ic_settings,
        description = "设置",
        backdropHost = backdropHost,
    ).apply {
        setOnClickListener {
            startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
        }
        layoutParams = LinearLayout.LayoutParams(dp(48), dp(48))
    }

    private fun dispatchBottomAction() {
        if (tabState.selectedTab == MainTab.QUOTA) {
            if (quotaActionEnabled && !quotaActionBusy) quotaPage.requestRefresh()
        } else if (usageActionEnabled && !usageActionBusy) {
            tokenUsagePage.requestSync()
        }
        updateBottomAction()
    }

    private fun updateBottomAction() {
        if (!::bottomBar.isInitialized) return
        if (tabState.selectedTab == MainTab.QUOTA) {
            bottomBar.setActionState(quotaActionEnabled, quotaActionBusy)
        } else {
            bottomBar.setActionState(usageActionEnabled, usageActionBusy)
        }
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
