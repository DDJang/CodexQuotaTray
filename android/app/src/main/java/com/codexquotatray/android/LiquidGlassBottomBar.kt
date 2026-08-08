package com.codexquotatray.android

import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.view.Choreographer
import android.view.Gravity
import android.view.MotionEvent
import android.view.VelocityTracker
import android.view.View
import android.view.ViewConfiguration
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt

/** Edge-to-edge bottom navigation with an AGSL/fallback liquid-glass renderer. */
internal class LiquidGlassBottomBar(
    context: Context,
    private val backdropHost: ViewGroup,
    private val palette: ThemePalette,
    private val onTabSelected: (MainTab) -> Unit,
) : FrameLayout(context) {
    private val barHeight = dp(68f).toInt()
    private val gestureSpacing = dp(10f).toInt()
    private val surfaceInset = dp(6f).toInt()
    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
    private val surface = FrameLayout(context).apply {
        clipChildren = false
        clipToPadding = false
        setBackgroundColor(Color.TRANSPARENT)
    }
    private val renderer = AgslLiquidGlassView(
        context = context,
        backdropHost = backdropHost,
        palette = palette,
        insetPx = surfaceInset.toFloat(),
        darkTheme = isDarkTheme(),
    )
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
    private var lensProgress = 0f
    private var safeBottomInset = 0
    private var progressSpring: ProgressSpring? = null
    private var velocityTracker: VelocityTracker? = null
    private var downX = 0f
    private var downProgress = 0f
    private var dragging = false
    private var lastBackdropRefreshNanos = 0L
    private var backdropRefreshPosted = false
    private var scrollListener: ViewTreeObserverScrollListener? = null
    private val layoutChangeListener = View.OnLayoutChangeListener { _, _, _, _, _, _, _, _, _ ->
        scheduleBackdropRefresh()
    }

    init {
        setClipChildren(false)
        clipToPadding = false
        setBackgroundColor(Color.TRANSPARENT)
        isClickable = true
        elevation = dp(3f)

        surface.addView(
            renderer,
            FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        tabRow.addView(quotaTab, tabLayoutParams())
        tabRow.addView(usageTab, tabLayoutParams())
        surface.addView(
            tabRow,
            FrameLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        addView(surface, FrameLayout.LayoutParams(dp(340f).toInt(), barHeight))
        applyTabVisuals()
        renderer.setLensProgress(0f, 0f)
        post {
            updateSurfaceGeometry()
            refreshBackdrop()
        }
    }

    fun requiredHeight(safeBottom: Int): Int = barHeight + gestureSpacing + safeBottom.coerceAtLeast(0)

    fun setSafeBottomInset(value: Int) {
        val next = value.coerceAtLeast(0)
        if (safeBottomInset == next && layoutParams?.height == requiredHeight(next)) return
        safeBottomInset = next
        (layoutParams as? FrameLayout.LayoutParams)?.let { params ->
            params.height = requiredHeight(next)
            layoutParams = params
        }
        requestLayout()
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        scrollListener = ViewTreeObserverScrollListener { scheduleBackdropRefresh() }
        scrollListener?.let { backdropHost.viewTreeObserver.addOnScrollChangedListener(it) }
        backdropHost.addOnLayoutChangeListener(layoutChangeListener)
        post {
            updateSurfaceGeometry()
            refreshBackdrop()
        }
    }

    override fun onDetachedFromWindow() {
        progressSpring?.cancel()
        velocityTracker?.recycle()
        velocityTracker = null
        scrollListener?.let { backdropHost.viewTreeObserver.removeOnScrollChangedListener(it) }
        backdropHost.removeOnLayoutChangeListener(layoutChangeListener)
        scrollListener = null
        super.onDetachedFromWindow()
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        updateSurfaceGeometry()
        if (width > 0 && height > 0) post { refreshBackdrop() }
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        if (visibility == View.VISIBLE) post { refreshBackdrop() }
    }

    fun setSelectedTab(tab: MainTab, animate: Boolean) {
        selectedTab = tab
        applyTabVisuals()
        val target = targetProgress(tab)
        if (!animate || width == 0 || height == 0) {
            progressSpring?.cancel()
            updateProgress(target, 0f)
        } else {
            progressSpring().animateTo(target, 0f, lensProgress)
        }
    }

    override fun onInterceptTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                beginTouch(event)
                dragging = false
                return false
            }

            MotionEvent.ACTION_MOVE -> {
                if (!dragging && abs(event.x - downX) > touchSlop) {
                    dragging = true
                    parent?.requestDisallowInterceptTouchEvent(true)
                    progressSpring?.cancel()
                    return true
                }
                if (!dragging) velocityTracker?.addMovement(event)
            }
        }
        return dragging
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                beginTouch(event)
                return true
            }

            MotionEvent.ACTION_MOVE -> {
                velocityTracker?.addMovement(event)
                if (!dragging && abs(event.x - downX) > touchSlop) {
                    dragging = true
                    progressSpring?.cancel()
                }
                if (dragging) {
                    val travel = lensTravel().toFloat().coerceAtLeast(1f)
                    val next = (downProgress + (event.x - downX) / travel).coerceIn(0f, 1f)
                    val stretch = (abs(event.x - downX) / travel).coerceIn(0f, 1f)
                    updateProgress(next, stretch)
                }
                return true
            }

            MotionEvent.ACTION_UP -> {
                velocityTracker?.addMovement(event)
                if (dragging) {
                    velocityTracker?.computeCurrentVelocity(1000)
                    val velocityX = velocityTracker?.xVelocity ?: 0f
                    finishDrag(velocityX)
                } else {
                    performClick()
                }
                endTouch()
                return true
            }

            MotionEvent.ACTION_CANCEL -> {
                if (dragging) finishDrag(0f)
                endTouch()
                return true
            }
        }
        return true
    }

    override fun performClick(): Boolean {
        super.performClick()
        return true
    }

    private fun beginTouch(event: MotionEvent) {
        velocityTracker?.recycle()
        velocityTracker = VelocityTracker.obtain()
        velocityTracker?.addMovement(event)
        downX = event.x
        downProgress = lensProgress
    }

    private fun endTouch() {
        velocityTracker?.recycle()
        velocityTracker = null
        dragging = false
        parent?.requestDisallowInterceptTouchEvent(false)
    }

    private fun finishDrag(velocityX: Float) {
        val travel = lensTravel().toFloat().coerceAtLeast(1f)
        val velocityProgress = velocityX / travel
        val flingThreshold = dp(950f)
        val target = when {
            abs(velocityX) >= flingThreshold -> if (velocityX > 0f) 1f else 0f
            lensProgress >= 0.5f -> 1f
            else -> 0f
        }
        val targetTab = if (target >= 0.5f) MainTab.USAGE else MainTab.QUOTA
        val changed = targetTab != selectedTab
        selectedTab = targetTab
        if (changed) onTabSelected(targetTab)
        applyTabVisuals()
        progressSpring().animateTo(
            target,
            velocityProgress.coerceIn(-4f, 4f),
            lensProgress,
        )
    }

    private fun progressSpring(): ProgressSpring = progressSpring ?: ProgressSpring(
        onUpdate = { value, velocity ->
            val springStretch = (abs(velocity) * 0.18f).coerceIn(0f, 1f)
            updateProgress(value, springStretch)
        },
        onEnd = { updateProgress(lensProgress, 0f) },
    ).also { progressSpring = it }

    private fun updateProgress(value: Float, stretch: Float) {
        lensProgress = value.coerceIn(0f, 1f)
        renderer.setLensProgress(lensProgress, stretch)
        applyTabVisuals()
    }

    private fun targetProgress(tab: MainTab): Float = if (tab == MainTab.USAGE) 1f else 0f

    private fun updateSurfaceGeometry() {
        if (width <= 0) return
        val available = (width - dp(48f).toInt()).coerceAtLeast(dp(200f).toInt())
        val surfaceWidth = min(dp(340f).toInt(), available)
        val surfaceParams = surface.layoutParams as? FrameLayout.LayoutParams
            ?: FrameLayout.LayoutParams(surfaceWidth, barHeight)
        surfaceParams.width = surfaceWidth
        surfaceParams.height = barHeight
        surfaceParams.gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
        surfaceParams.topMargin = 0
        surface.layoutParams = surfaceParams
        renderer.setLensProgress(lensProgress, 0f)
    }

    private fun lensTravel(): Int {
        val innerWidth = ((surface.layoutParams as? FrameLayout.LayoutParams)?.width ?: width) -
            surfaceInset * 2
        return innerWidth.coerceAtLeast(0) / 2
    }

    private fun refreshBackdrop() {
        backdropRefreshPosted = false
        renderer.refreshBackdrop()
        lastBackdropRefreshNanos = System.nanoTime()
    }

    private fun scheduleBackdropRefresh() {
        val now = System.nanoTime()
        val intervalNanos = 50_000_000L
        if (now - lastBackdropRefreshNanos >= intervalNanos) {
            refreshBackdrop()
            return
        }
        if (backdropRefreshPosted) return
        backdropRefreshPosted = true
        val delayMillis = ((intervalNanos - (now - lastBackdropRefreshNanos)) / 1_000_000L)
            .coerceAtLeast(1L)
        postDelayed({ refreshBackdrop() }, delayMillis)
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
        val quotaWeight = 1f - lensProgress
        applyTabVisual(quotaTab, quotaIcon, quotaLabel, quotaWeight)
        applyTabVisual(usageTab, usageIcon, usageLabel, lensProgress)
    }

    private fun applyTabVisual(item: View, icon: ImageView, label: TextView, weight: Float) {
        val selected = weight >= 0.5f
        item.isSelected = selected
        item.isActivated = selected
        val color = blendColor(palette.secondary, palette.accent, weight)
        icon.imageTintList = ColorStateList.valueOf(color)
        label.setTextColor(color)
        label.alpha = 0.78f + weight * 0.22f
        label.setTypeface(label.typeface, if (selected) Typeface.BOLD else Typeface.NORMAL)
        item.background = null
    }

    private fun blendColor(from: Int, to: Int, amount: Float): Int {
        val t = amount.coerceIn(0f, 1f)
        return Color.rgb(
            (Color.red(from) + (Color.red(to) - Color.red(from)) * t).toInt(),
            (Color.green(from) + (Color.green(to) - Color.green(from)) * t).toInt(),
            (Color.blue(from) + (Color.blue(to) - Color.blue(from)) * t).toInt(),
        )
    }

    private fun tabLayoutParams(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(0, LayoutParams.MATCH_PARENT, 1f)

    private fun isDarkTheme(): Boolean = AppTheme.effectiveMode(context) == ThemeMode.DARK

    private fun dp(value: Float): Float = value * resources.displayMetrics.density

    private class ViewTreeObserverScrollListener(
        private val callback: () -> Unit,
    ) : android.view.ViewTreeObserver.OnScrollChangedListener {
        override fun onScrollChanged() = callback()
    }

    /** Choreographer spring; no duration-based linear interpolation. */
    private class ProgressSpring(
        private val onUpdate: (value: Float, velocity: Float) -> Unit,
        private val onEnd: () -> Unit,
    ) : Choreographer.FrameCallback {
        private val choreographer = Choreographer.getInstance()
        private val stiffness = 680f
        private val damping = 2f * sqrt(stiffness) * 0.9f
        private var target = 0f
        private var position = 0f
        private var velocity = 0f
        private var lastFrameNanos = 0L
        private var running = false

        fun animateTo(nextTarget: Float, initialVelocity: Float, startPosition: Float) {
            target = nextTarget.coerceIn(0f, 1f)
            if (!running) {
                position = startPosition.coerceIn(0f, 1f)
                velocity = initialVelocity
                running = true
                lastFrameNanos = 0L
                choreographer.postFrameCallback(this)
            } else if (abs(initialVelocity) > 0.01f) {
                velocity = initialVelocity
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
            onUpdate(position, velocity)
            if (abs(target - position) < 0.0018f && abs(velocity) < 0.02f) {
                position = target
                velocity = 0f
                running = false
                lastFrameNanos = 0L
                onUpdate(position, velocity)
                onEnd()
            } else {
                choreographer.postFrameCallback(this)
            }
        }
    }
}
