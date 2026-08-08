package com.codexquotatray.android

import android.animation.ValueAnimator
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.StateListDrawable
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import kotlin.math.min

/** Shared icon control used by the main header, Settings back, and refresh. */
internal class LiquidGlassIconButton(
    context: Context,
    private val palette: ThemePalette,
    private val iconRes: Int,
    description: String,
    private val backdropHost: ViewGroup? = null,
) : FrameLayout(context) {
    private val density = resources.displayMetrics.density
    private val glassSurface = LiquidGlassSurfaceView(
        context = context,
        backdropHost = backdropHost,
        palette = palette,
        darkTheme = isDark(palette),
        fallbackTint = palette.surface,
    )
    private val glassPressed = withAlpha(palette.accent, if (isDark(palette)) 72 else 48)
    private val icon = if (iconRes != 0) ImageView(context).apply {
        setImageResource(iconRes)
        imageTintList = ColorStateList.valueOf(palette.title)
        contentDescription = description
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_YES
    } else {
        null
    }
    private var busyState = false

    init {
        setWillNotDraw(false)
        isClickable = true
        isFocusable = true
        contentDescription = description
        minimumWidth = dp(48)
        minimumHeight = dp(48)
        clipChildren = true
        addView(glassSurface, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        icon?.let { addView(it, LayoutParams(dp(24), dp(24), Gravity.CENTER)) }
        // Exclude the complete control when its source is an ancestor. This
        // avoids feeding the button's own icon back into its refraction pass.
        (backdropHost as? LiquidGlassExclusionHost)?.registerLiquidGlassView(this)
        setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> animate().scaleX(0.95f).scaleY(0.95f).setDuration(90).start()
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL ->
                    animate().scaleX(1f).scaleY(1f).setDuration(150).start()
            }
            false
        }
    }

    fun setBusy(busy: Boolean) {
        if (busyState == busy) return
        busyState = busy
        icon?.animate()?.cancel()
        if (busy) {
            // One restrained turn gives immediate feedback without an
            // always-spinning control while the network request is pending.
            icon?.animate()?.rotationBy(360f)?.setDuration(620L)?.setInterpolator(
                android.view.animation.LinearInterpolator(),
            )?.setListener(null)?.start()
        } else {
            icon?.animate()?.setDuration(120L)?.rotation(0f)?.start()
        }
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        (backdropHost as? LiquidGlassExclusionHost)?.registerLiquidGlassView(this)
    }

    override fun onDetachedFromWindow() {
        (backdropHost as? LiquidGlassExclusionHost)?.unregisterLiquidGlassView(this)
        super.onDetachedFromWindow()
    }

    override fun onDrawForeground(canvas: Canvas) {
        super.onDrawForeground(canvas)
        if (isPressed) {
            val radius = min(width, height) / 2f
            Paint(Paint.ANTI_ALIAS_FLAG).apply {
                color = glassPressed
                style = Paint.Style.FILL
                canvas.drawRoundRect(
                    RectF(0f, 0f, width.toFloat(), height.toFloat()),
                    radius,
                    radius,
                    this,
                )
            }
        }
        if (iconRes == 0) drawBackArrow(canvas)
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        glassSurface.configure(
            radiusPx = min(width, height) / 2f,
            refractionHeightPx = dpFloat(22f),
            refractionOffsetPx = dpFloat(34f),
            tintAlpha = if (isDark(palette)) 0.22f else 0.28f,
            dispersion = 0.13f,
            blurRadiusPx = dpFloat(1.1f),
            touchEffect = false,
        )
    }

    private fun drawBackArrow(canvas: Canvas) {
        val arrow = Path()
        val centerX = width / 2f
        val centerY = height / 2f
        val tipX = centerX - dpFloat(5f)
        val tailX = centerX + dpFloat(6f)
        val wingX = centerX - dpFloat(1f)
        val wingY = dpFloat(5f)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = palette.title
            style = Paint.Style.STROKE
            strokeWidth = dpFloat(2.1f)
            strokeCap = Paint.Cap.ROUND
            strokeJoin = Paint.Join.ROUND
        }
        arrow.moveTo(tipX, centerY)
        arrow.lineTo(tailX, centerY)
        arrow.moveTo(tipX, centerY)
        arrow.lineTo(wingX, centerY - wingY)
        arrow.moveTo(tipX, centerY)
        arrow.lineTo(wingX, centerY + wingY)
        canvas.drawPath(arrow, paint)
    }

    private fun isDark(palette: ThemePalette): Boolean =
        Color.luminance(palette.background) < 0.35f

    private fun withAlpha(color: Int, alpha: Int): Int =
        Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color))

    private fun dp(value: Int): Int = (value * density).toInt().coerceAtLeast(value)
    private fun dpFloat(value: Float): Float = value * density
}

/** Native track with a QmDeve LiquidGlass thumb and a deterministic fallback. */
internal class LiquidGlassToggleView(
    context: Context,
    private val palette: ThemePalette,
    backdropHost: ViewGroup? = null,
) : FrameLayout(context) {
    private val density = resources.displayMetrics.density
    private val trackPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE }
    private val thumbPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val thumbGlass = LiquidGlassSurfaceView(
        context = context,
        backdropHost = backdropHost,
        palette = palette,
        darkTheme = isDark(palette),
        fallbackTint = palette.surface,
    )
    private var progress = 0f
    private var checkedValue = false
    private var animator: ValueAnimator? = null
    private var listener: ((LiquidGlassToggleView, Boolean) -> Unit)? = null

    var isChecked: Boolean
        get() = checkedValue
        set(value) = updateChecked(value, notify = true, animate = true)

    init {
        isClickable = true
        isFocusable = true
        minimumWidth = dp(56)
        minimumHeight = dp(36)
        contentDescription = "开关"
        clipChildren = false
        addView(thumbGlass, LayoutParams(dp(1), dp(1)))
    }

    fun setOnCheckedChangeListener(callback: ((LiquidGlassToggleView, Boolean) -> Unit)?) {
        listener = callback
    }

    fun setCheckedSilently(value: Boolean) {
        updateChecked(value, notify = false, animate = false)
    }

    fun toggle() {
        isChecked = !checkedValue
    }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = resolveSize(dp(56), widthMeasureSpec)
        val height = resolveSize(dp(36), heightMeasureSpec)
        setMeasuredDimension(width, height)
        updateGlassGeometry()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val trackHeight = height.toFloat().coerceAtMost(dpFloat(32f))
        val trackWidth = width.toFloat().coerceAtLeast(dpFloat(52f))
        val left = (width - trackWidth) / 2f
        val top = (height - trackHeight) / 2f
        val radius = trackHeight / 2f
        val track = RectF(left, top, left + trackWidth, top + trackHeight)
        trackPaint.color = lerpColor(
            withAlpha(palette.title, 34),
            withAlpha(palette.accent, 132),
            progress,
        )
        canvas.drawRoundRect(track, radius, radius, trackPaint)
        strokePaint.strokeWidth = dpFloat(1f)
        strokePaint.color = lerpColor(
            withAlpha(palette.title, 70),
            withAlpha(Color.WHITE, 112),
            progress,
        )
        canvas.drawRoundRect(track, radius, radius, strokePaint)

        val thumbRadius = (radius - dpFloat(3f)).coerceAtLeast(dpFloat(8f))
        val travel = trackWidth - thumbRadius * 2f - dpFloat(6f)
        val centerX = left + thumbRadius + dpFloat(3f) + travel * progress
        val centerY = top + radius
        thumbPaint.shader = LinearGradient(
            centerX - thumbRadius,
            centerY - thumbRadius,
            centerX + thumbRadius,
            centerY + thumbRadius,
            if (progress > 0.5f) Color.WHITE else withAlpha(palette.surface, 210),
            if (progress > 0.5f) withAlpha(palette.surface, 210) else withAlpha(palette.title, 140),
            Shader.TileMode.CLAMP,
        )
        thumbPaint.alpha = if (isPressed) 170 else 210
        canvas.drawCircle(centerX, centerY, thumbRadius, thumbPaint)
        thumbPaint.shader = null
        updateGlassGeometry(trackWidth, trackHeight, centerX, centerY, thumbRadius)
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        updateGlassGeometry()
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                isPressed = true
                invalidate()
                return true
            }
            MotionEvent.ACTION_UP -> {
                isPressed = false
                performClick()
                return true
            }
            MotionEvent.ACTION_CANCEL -> {
                isPressed = false
                invalidate()
                return true
            }
        }
        return true
    }

    override fun performClick(): Boolean {
        super.performClick()
        toggle()
        return true
    }

    private fun updateChecked(value: Boolean, notify: Boolean, animate: Boolean) {
        val changed = checkedValue != value
        checkedValue = value
        val target = if (value) 1f else 0f
        animator?.cancel()
        if (animate && isLaidOut) {
            animator = ValueAnimator.ofFloat(progress, target).apply {
                duration = 220L
                addUpdateListener {
                    progress = it.animatedValue as Float
                    updateGlassGeometry()
                    invalidate()
                }
                start()
            }
        } else {
            progress = target
            updateGlassGeometry()
            invalidate()
        }
        if (notify && changed) listener?.invoke(this, value)
    }

    private fun updateGlassGeometry(
        measuredTrackWidth: Float = width.toFloat().coerceAtLeast(dpFloat(52f)),
        measuredTrackHeight: Float = height.toFloat().coerceAtMost(dpFloat(32f)),
        measuredCenterX: Float? = null,
        measuredCenterY: Float? = null,
        measuredThumbRadius: Float? = null,
    ) {
        if (width <= 0 || height <= 0) return
        val trackLeft = (width - measuredTrackWidth) / 2f
        val trackTop = (height - measuredTrackHeight) / 2f
        val trackRadius = measuredTrackHeight / 2f
        val thumbRadius = measuredThumbRadius ?: (trackRadius - dpFloat(3f)).coerceAtLeast(dpFloat(8f))
        val centerX = measuredCenterX ?: run {
            val travel = measuredTrackWidth - thumbRadius * 2f - dpFloat(6f)
            trackLeft + thumbRadius + dpFloat(3f) + travel * progress
        }
        val centerY = measuredCenterY ?: trackTop + trackRadius
        val diameter = (thumbRadius * 2f).toInt().coerceAtLeast(dp(16))
        val left = (centerX - thumbRadius).toInt()
        val top = (centerY - thumbRadius).toInt()
        val params = thumbGlass.layoutParams as? FrameLayout.LayoutParams
            ?: FrameLayout.LayoutParams(diameter, diameter)
        if (params.width != diameter || params.height != diameter ||
            params.leftMargin != left || params.topMargin != top
        ) {
            params.width = diameter
            params.height = diameter
            params.leftMargin = left
            params.topMargin = top
            thumbGlass.layoutParams = params
        }
        thumbGlass.configure(
            radiusPx = thumbRadius,
            refractionHeightPx = dpFloat(18f),
            refractionOffsetPx = dpFloat(28f),
            tintAlpha = if (progress > 0.5f) 0.34f else 0.28f,
            dispersion = 0.12f,
            blurRadiusPx = dpFloat(0.9f),
            touchEffect = false,
        )
    }

    private fun lerpColor(from: Int, to: Int, amount: Float): Int {
        val t = amount.coerceIn(0f, 1f)
        return Color.argb(
            (Color.alpha(from) + (Color.alpha(to) - Color.alpha(from)) * t).toInt(),
            (Color.red(from) + (Color.red(to) - Color.red(from)) * t).toInt(),
            (Color.green(from) + (Color.green(to) - Color.green(from)) * t).toInt(),
            (Color.blue(from) + (Color.blue(to) - Color.blue(from)) * t).toInt(),
        )
    }

    private fun withAlpha(color: Int, alpha: Int): Int =
        Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color))

    private fun isDark(palette: ThemePalette): Boolean =
        Color.luminance(palette.background) < 0.35f

    private fun dp(value: Int): Int = (value * density).toInt().coerceAtLeast(value)
    private fun dpFloat(value: Float): Float = value * density
}

/** Rounded elevated container shared by all Settings sections. */
internal class SettingsGroupCard(
    context: Context,
    private val palette: ThemePalette,
) : LinearLayout(context) {
    private val density = resources.displayMetrics.density
    private val dividerColor = Color.argb(
        if (isDark()) 38 else 28,
        Color.red(palette.title),
        Color.green(palette.title),
        Color.blue(palette.title),
    )

    init {
        orientation = VERTICAL
        clipChildren = false
        clipToPadding = false
        setPadding(dp(4), dp(4), dp(4), dp(4))
        background = GradientDrawable().apply {
            setColor(palette.surface)
            cornerRadius = dpFloat(26f)
        }
    }

    fun addItem(view: View, dividerBefore: Boolean = childCount > 0) {
        if (dividerBefore && childCount > 0) addDivider()
        addView(view, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))
    }

    fun addContent(view: View, bottomMargin: Int = 0, dividerBefore: Boolean = false) {
        if (dividerBefore && childCount > 0) addDivider()
        addView(view, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
            if (bottomMargin > 0) this.bottomMargin = dp(bottomMargin)
        })
    }

    private fun addDivider() {
        addView(View(context).apply { setBackgroundColor(dividerColor) }, LayoutParams(
            LayoutParams.MATCH_PARENT,
            dp(1),
        ).apply {
            leftMargin = dp(14)
            rightMargin = dp(14)
        })
    }

    private fun isDark(): Boolean = Color.luminance(palette.background) < 0.35f
    private fun dp(value: Int): Int = (value * density).toInt().coerceAtLeast(value)
    private fun dpFloat(value: Float): Float = value * density
}

/** A two-line settings row with optional trailing control and chevron. */
internal class SettingsRow(
    context: Context,
    palette: ThemePalette,
    title: String,
    summary: String? = null,
    trailing: View? = null,
    showChevron: Boolean = false,
    onClick: (() -> Unit)? = null,
) : LinearLayout(context) {
    private val density = resources.displayMetrics.density

    init {
        orientation = HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(16), if (summary == null) dp(9) else dp(11), dp(12), if (summary == null) dp(9) else dp(11))
        minimumHeight = dp(if (summary == null) 58 else 72)
        if (onClick != null) {
            isClickable = true
            isFocusable = true
            background = pressedBackground(palette)
            setOnClickListener { onClick() }
        }

        val labels = LinearLayout(context).apply {
            orientation = VERTICAL
            gravity = Gravity.CENTER_VERTICAL
        }
        labels.addView(TextView(context).apply {
            text = title
            textSize = 16f
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(palette.body)
        })
        summary?.let {
            labels.addView(TextView(context).apply {
                text = it
                textSize = 12.5f
                setTextColor(palette.muted)
                setPadding(0, dp(3), 0, 0)
            })
        }
        addView(labels, LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
        trailing?.let {
            if (it is LiquidGlassToggleView) {
                it.minimumWidth = dp(56)
                it.minimumHeight = dp(36)
                it.visibility = VISIBLE
                it.alpha = 1f
                addView(it, LayoutParams(dp(56), dp(36)))
            } else {
                it.minimumWidth = dp(52)
                addView(it, LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT))
            }
        }
        if (showChevron) {
            addView(TextView(context).apply {
                text = "›"
                textSize = 26f
                gravity = Gravity.CENTER
                setTextColor(withAlpha(palette.muted, 210))
            }, LayoutParams(dp(24), dp(40)))
        }
    }

    private fun pressedBackground(palette: ThemePalette): StateListDrawable = StateListDrawable().apply {
        addState(intArrayOf(android.R.attr.state_pressed), GradientDrawable().apply {
            setColor(withAlpha(palette.accent, if (isDark(palette)) 30 else 22))
            cornerRadius = dpFloat(18f)
        })
        addState(intArrayOf(), GradientDrawable().apply {
            setColor(Color.TRANSPARENT)
            cornerRadius = dpFloat(18f)
        })
    }

    private fun isDark(palette: ThemePalette): Boolean =
        Color.luminance(palette.background) < 0.35f

    private fun withAlpha(color: Int, alpha: Int): Int =
        Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color))

    private fun dp(value: Int): Int = (value * density).toInt().coerceAtLeast(value)
    private fun dpFloat(value: Float): Float = value * density
}

internal fun settingsSectionLabel(context: Context, palette: ThemePalette, title: String): TextView =
    TextView(context).apply {
        text = title
        textSize = 12f
        letterSpacing = 0.08f
        setTypeface(typeface, Typeface.BOLD)
        setTextColor(Color.argb(
            if (Color.luminance(palette.background) < 0.35f) 178 else 150,
            Color.red(palette.secondary),
            Color.green(palette.secondary),
            Color.blue(palette.secondary),
        ))
        setPadding(dp(context, 4), 0, 0, 0)
    }

internal class GlassActionButton(
    context: Context,
    private val palette: ThemePalette,
    private val danger: Boolean = false,
) : TextView(context) {
    private val density = resources.displayMetrics.density

    init {
        setWillNotDraw(false)
        isClickable = true
        isFocusable = true
        gravity = Gravity.CENTER
        textSize = 14f
        setTypeface(typeface, Typeface.BOLD)
        minimumHeight = dp(50)
        setPadding(dp(16), dp(10), dp(16), dp(10))
        setTextColor(if (danger) palette.error else palette.secondaryButtonText)
        setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> animate().scaleX(0.98f).scaleY(0.98f).setDuration(80).start()
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL ->
                    animate().scaleX(1f).scaleY(1f).setDuration(150).start()
            }
            false
        }
    }

    override fun onDraw(canvas: Canvas) {
        val radius = dpFloat(17f)
        val fill = if (isPressed) {
            withAlpha(if (danger) palette.error else palette.accent, if (danger) 34 else 36)
        } else {
            withAlpha(if (danger) palette.error else palette.secondaryButton, if (danger) 20 else 120)
        }
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = fill }
        canvas.drawRoundRect(RectF(0f, 0f, width.toFloat(), height.toFloat()), radius, radius, paint)
        paint.style = Paint.Style.STROKE
        paint.strokeWidth = dpFloat(1f)
        paint.color = withAlpha(if (danger) palette.error else palette.title, if (danger) 78 else 32)
        canvas.drawRoundRect(
            RectF(dpFloat(0.5f), dpFloat(0.5f), width - dpFloat(0.5f), height - dpFloat(0.5f)),
            radius,
            radius,
            paint,
        )
        super.onDraw(canvas)
    }

    override fun drawableStateChanged() {
        super.drawableStateChanged()
        invalidate()
    }

    private fun withAlpha(color: Int, alpha: Int): Int =
        Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color))

    private fun dp(value: Int): Int = (value * density).toInt().coerceAtLeast(value)
    private fun dpFloat(value: Float): Float = value * density
}

private fun dp(context: Context, value: Int): Int =
    (value * context.resources.displayMetrics.density).toInt().coerceAtLeast(value)
