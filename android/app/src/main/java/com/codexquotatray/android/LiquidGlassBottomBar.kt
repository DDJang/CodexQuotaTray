package com.codexquotatray.android

import android.app.ActivityManager
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.view.Choreographer
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import com.matrix.prismal.PrismalFrameLayout
import com.matrix.prismal.PrismalLiquidGlass
import kotlin.math.abs
import kotlin.math.sqrt
import kotlin.math.min

/** Native-View liquid glass navigation with a small, bounded capture surface. */
internal class LiquidGlassBottomBar(
    context: Context,
    private val backdropHost: ViewGroup,
    private val palette: ThemePalette,
    private val onTabSelected: (MainTab) -> Unit,
) : FrameLayout(context) {
    private data class PrismalPair(
        val outer: PrismalFrameLayout,
        val lens: PrismalFrameLayout,
    )

    private val barHeight = dp(68f).toInt()
    private val surfaceInset = dp(6f).toInt()
    private val prismalPair = createPrismalPair()
    private val surface = FrameLayout(context).apply {
        clipChildren = false
        clipToPadding = false
        setBackgroundColor(Color.TRANSPARENT)
    }
    private val outerLayer: View = prismalPair?.outer ?: fallbackGlass(isLens = false)
    private val lensLayer: View = prismalPair?.lens ?: fallbackGlass(isLens = true)
    private val tabRow = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER
        setPadding(surfaceInset, surfaceInset, surfaceInset, surfaceInset)
        clipChildren = false
        clipToPadding = false
    }
    private val quotaTab = tabItem("额度", R.drawable.ic_quota, "切换到额度页面") {
        onTabSelected(MainTab.QUOTA)
    }
    private val usageTab = tabItem("统计", R.drawable.ic_usage, "切换到统计页面") {
        onTabSelected(MainTab.USAGE)
    }
    private val quotaIcon = quotaTab.getChildAt(0) as ImageView
    private val usageIcon = usageTab.getChildAt(0) as ImageView
    private val quotaLabel = quotaTab.getChildAt(1) as TextView
    private val usageLabel = usageTab.getChildAt(1) as TextView
    private var selectedTab = MainTab.QUOTA
    private var lensSpring: LiquidSpring? = null
    private var lastBackdropRefreshNanos = 0L
    private var backdropRefreshPosted = false
    private var scrollListener: ViewTreeObserverScrollListener? = null

    init {
        setClipChildren(false)
        clipToPadding = false
        setBackgroundColor(Color.TRANSPARENT)
        elevation = dp(3f)

        surface.addView(
            outerLayer,
            FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        surface.addView(
            lensLayer,
            FrameLayout.LayoutParams(0, 0),
        )
        tabRow.addView(quotaTab, tabLayoutParams())
        tabRow.addView(usageTab, tabLayoutParams())
        surface.addView(
            tabRow,
            FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        addView(surface, FrameLayout.LayoutParams(dp(340f).toInt(), barHeight))
        applyTabVisuals()
        post {
            updateSurfaceGeometry()
            refreshBackdrops(includeOuter = true)
        }
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        scrollListener = ViewTreeObserverScrollListener { scheduleBackdropRefresh(includeOuter = true) }
        backdropHost.viewTreeObserver.addOnScrollChangedListener(scrollListener)
        post {
            updateSurfaceGeometry()
            refreshBackdrops(includeOuter = true)
        }
    }

    override fun onDetachedFromWindow() {
        scrollListener?.let { backdropHost.viewTreeObserver.removeOnScrollChangedListener(it) }
        scrollListener = null
        lensSpring?.cancel()
        super.onDetachedFromWindow()
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        updateSurfaceGeometry()
        if (width > 0 && height > 0) {
            post { refreshBackdrops(includeOuter = true) }
        }
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        if (visibility == View.VISIBLE) {
            post { refreshBackdrops(includeOuter = true) }
        }
    }

    fun setSelectedTab(tab: MainTab, animate: Boolean) {
        selectedTab = tab
        applyTabVisuals()
        if (width == 0 || height == 0) {
            post { moveLens(animate = false) }
        } else {
            moveLens(animate)
        }
    }

    private fun moveLens(animate: Boolean) {
        updateSurfaceGeometry()
        val target = if (selectedTab == MainTab.USAGE) lensTravel().toFloat() else 0f
        if (!animate) {
            lensSpring?.cancel()
            lensLayer.translationX = target
            refreshBackdrops(includeOuter = false)
        } else {
            lensAnimation().animateTo(target)
        }
    }

    private fun lensAnimation(): LiquidSpring {
        return lensSpring ?: LiquidSpring(
            view = lensLayer,
            onUpdate = { scheduleBackdropRefresh(includeOuter = false) },
            onEnd = { refreshBackdrops(includeOuter = false) },
        ).also { lensSpring = it }
    }

    private fun updateSurfaceGeometry() {
        if (width <= 0) return
        val available = (width - dp(48f).toInt()).coerceAtLeast(dp(200f).toInt())
        val surfaceWidth = min(dp(340f).toInt(), available)
        val surfaceParams = surface.layoutParams as? FrameLayout.LayoutParams
            ?: FrameLayout.LayoutParams(surfaceWidth, barHeight)
        surfaceParams.width = surfaceWidth
        surfaceParams.height = barHeight
        surfaceParams.gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
        surfaceParams.topMargin = dp(4f).toInt()
        surface.layoutParams = surfaceParams

        val lensParams = lensLayer.layoutParams as FrameLayout.LayoutParams
        lensParams.width = lensTravel()
        lensParams.height = barHeight - surfaceInset * 2
        lensParams.leftMargin = surfaceInset
        lensParams.topMargin = surfaceInset
        lensLayer.layoutParams = lensParams
        lensLayer.translationX = if (selectedTab == MainTab.USAGE) lensTravel().toFloat() else 0f
        configurePrismalSize()
    }

    private fun lensTravel(): Int {
        val innerWidth = ((surface.layoutParams as? FrameLayout.LayoutParams)?.width ?: width) -
            surfaceInset * 2
        return innerWidth.coerceAtLeast(0) / 2
    }

    private fun configurePrismalSize() {
        val pair = prismalPair ?: return
        pair.outer.setCornerRadius(barHeight / 2f)
        pair.lens.setCornerRadius((barHeight - surfaceInset * 2) / 2f)
    }

    private fun refreshBackdrops(includeOuter: Boolean) {
        val pair = prismalPair ?: return
        if (includeOuter) pair.outer.updateBackground()
        pair.lens.updateBackground()
        lastBackdropRefreshNanos = System.nanoTime()
    }

    private fun scheduleBackdropRefresh(includeOuter: Boolean) {
        val now = System.nanoTime()
        val intervalNanos = 40_000_000L
        if (now - lastBackdropRefreshNanos >= intervalNanos) {
            refreshBackdrops(includeOuter)
            return
        }
        if (backdropRefreshPosted) return
        backdropRefreshPosted = true
        val delayMillis = ((intervalNanos - (now - lastBackdropRefreshNanos)) / 1_000_000L)
            .coerceAtLeast(1L)
        postDelayed({
            backdropRefreshPosted = false
            refreshBackdrops(includeOuter)
        }, delayMillis)
    }

    private fun createPrismalPair(): PrismalPair? {
        if (!supportsOpenGl()) return null
        return runCatching {
            val outer = PrismalFrameLayout(context)
            val lens = PrismalFrameLayout(context)
            PrismalLiquidGlass.applyBase(outer)
            PrismalLiquidGlass.applyBase(lens)
            configureGlass(outer, isLens = false)
            configureGlass(lens, isLens = true)
            PrismalPair(outer, lens)
        }.getOrNull()
    }

    private fun configureGlass(glass: PrismalFrameLayout, isLens: Boolean) {
        glass.setCaptureHost(backdropHost)
        glass.setThickness(dp(if (isLens) 6f else 7f).toFloat())
        glass.setBlurRadius(dp(if (isLens) 2.6f else 3.6f).toFloat())
        glass.setIOR(if (isLens) 1.52f else 1.48f)
        glass.setNormalStrength(if (isLens) 1.12f else 0.94f)
        glass.setDisplacementScale(if (isLens) 0.72f else 0.48f)
        glass.setLensRefractionScale(if (isLens) 0.62f else 0.42f)
        glass.setLiquidDomeStrength(if (isLens) 0.78f else 0.58f)
        glass.setFresnelReflectStrength(if (isLens) 0.96f else 0.68f)
        glass.setRimStrength(if (isLens) 0.32f else 0.18f)
        glass.setHighlightWidth(if (isLens) 0.55f else 0.38f)
        glass.setSpecular(if (isLens) 0.9f else 0.55f, if (isLens) 110f else 90f)
        glass.setCausticIntensity(if (isLens) 0.1f else 0.06f)
        glass.setChromaticAberration(if (isLens) 0.45f else 0.18f)
        glass.setBrightness(if (isLens) 1.08f else 1.03f)
        glass.setTransmittance(if (isLens) 0.94f else 0.9f)
        glass.setGlassColor(
            if (isLens) withAlpha(palette.accent, 34)
            else withAlpha(Color.WHITE, if (isDarkTheme()) 24 else 54),
        )
        glass.setShadowProperties(withAlpha(Color.BLACK, if (isLens) 28 else 20), 0.18f)
        glass.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO)
    }

    private fun fallbackGlass(isLens: Boolean): View = FrameLayout(context).apply {
        setBackground(
            GradientDrawable().apply {
                setColor(
                    if (isLens) withAlpha(palette.accent, if (isDarkTheme()) 50 else 34)
                    else withAlpha(if (isDarkTheme()) Color.WHITE else Color.WHITE, if (isDarkTheme()) 30 else 112),
                )
                setStroke(dp(1f).toInt(), withAlpha(palette.title, if (isLens) 30 else 20))
                cornerRadius = dp(if (isLens) 28f else 34f).toFloat()
            },
        )
        isClickable = false
        importantForAccessibility = View.IMPORTANT_FOR_ACCESSIBILITY_NO
    }

    private fun tabItem(
        label: String,
        icon: Int,
        description: String,
        action: () -> Unit,
    ): LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        gravity = Gravity.CENTER
        isClickable = true
        isFocusable = true
        minimumHeight = dp(48f).toInt()
        contentDescription = description
        setOnClickListener { action() }
        addView(ImageView(context).apply {
            setImageResource(icon)
            contentDescription = description
            layoutParams = LinearLayout.LayoutParams(dp(23f).toInt(), dp(23f).toInt())
        })
        addView(TextView(context).apply {
            text = label
            textSize = 12f
            gravity = Gravity.CENTER
            setPadding(0, dp(1f).toInt(), 0, 0)
        })
    }

    private fun applyTabVisuals() {
        applyTabVisual(quotaTab, quotaIcon, quotaLabel, selectedTab == MainTab.QUOTA)
        applyTabVisual(usageTab, usageIcon, usageLabel, selectedTab == MainTab.USAGE)
    }

    private fun applyTabVisual(
        item: LinearLayout,
        icon: ImageView,
        label: TextView,
        selected: Boolean,
    ) {
        item.isSelected = selected
        item.isActivated = selected
        val color = if (selected) palette.accent else palette.secondary
        icon.imageTintList = ColorStateList.valueOf(color)
        label.setTextColor(color)
        label.setTypeface(label.typeface, if (selected) Typeface.BOLD else Typeface.NORMAL)
        item.background = null
    }

    private fun tabLayoutParams(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, LayoutParams.MATCH_PARENT, 1f)

    private fun supportsOpenGl(): Boolean {
        val manager = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
            ?: return true
        val version = manager.deviceConfigurationInfo.reqGlEsVersion
        return version == 0 || version >= 0x20000
    }

    private fun isDarkTheme(): Boolean = AppTheme.effectiveMode(context) == ThemeMode.DARK

    private fun withAlpha(color: Int, alpha: Int): Int =
        Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color))

    private fun dp(value: Float): Float = value * resources.displayMetrics.density

    /** Keeps the dependency-free Android listener type out of the public API. */
    private class ViewTreeObserverScrollListener(
        private val callback: () -> Unit,
    ) : android.view.ViewTreeObserver.OnScrollChangedListener {
        override fun onScrollChanged() = callback()
    }

    /** A critically damped, Choreographer-driven spring with no linear interpolation. */
    private class LiquidSpring(
        private val view: View,
        private val onUpdate: () -> Unit,
        private val onEnd: () -> Unit,
    ) : Choreographer.FrameCallback {
        private val choreographer = Choreographer.getInstance()
        private val stiffness = 650f
        private val damping = 2f * sqrt(stiffness) * 0.92f
        private var target = view.translationX
        private var position = view.translationX
        private var velocity = 0f
        private var lastFrameNanos = 0L
        private var running = false

        fun animateTo(target: Float) {
            this.target = target
            if (!running) {
                running = true
                lastFrameNanos = 0L
                choreographer.postFrameCallback(this)
            }
        }

        fun cancel() {
            if (!running) return
            running = false
            choreographer.removeFrameCallback(this)
            lastFrameNanos = 0L
        }

        override fun doFrame(frameTimeNanos: Long) {
            if (!running) return
            val dt = if (lastFrameNanos == 0L) {
                1f / 60f
            } else {
                ((frameTimeNanos - lastFrameNanos) / 1_000_000_000f).coerceIn(0.008f, 0.032f)
            }
            lastFrameNanos = frameTimeNanos
            val acceleration = (target - position) * stiffness - velocity * damping
            velocity += acceleration * dt
            position += velocity * dt
            view.translationX = position
            onUpdate()
            if (abs(target - position) < 0.35f && abs(velocity) < 2.5f) {
                position = target
                view.translationX = target
                running = false
                lastFrameNanos = 0L
                onEnd()
            } else {
                choreographer.postFrameCallback(this)
            }
        }
    }
}
