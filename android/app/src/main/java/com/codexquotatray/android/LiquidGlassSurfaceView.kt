package com.codexquotatray.android

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.os.Build
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import com.qmdeve.liquidglass.widget.LiquidGlassView
import kotlin.math.min

/**
 * Shared wrapper around the official QmDeve LiquidGlassView.
 *
 * API 33+ binds directly to the real source ViewGroup. API 29-32 draw the
 * same rounded, tinted native surface as a deliberately quiet fallback; no
 * bitmap or AGSL renderer is kept in the app.
 */
@SuppressLint("NewApi")
internal class LiquidGlassSurfaceView(
    context: Context,
    private val backdropHost: ViewGroup?,
    private val palette: ThemePalette,
    private val darkTheme: Boolean,
    private val fallbackTint: Int = palette.surface,
) : FrameLayout(context) {
    private val density = resources.displayMetrics.density
    private val fullGlassSupported = Build.VERSION.SDK_INT >= 33
    private val glassView: LiquidGlassView? = if (fullGlassSupported) {
        LiquidGlassView(context)
    } else {
        null
    }
    private val fallbackFill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val fallbackStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(1f)
    }
    private var cornerRadiusPx = dp(24f)
    private var refractionHeightPx = dp(28f)
    private var refractionOffsetPx = dp(42f)
    private var tintAlpha = if (darkTheme) 0.18f else 0.24f
    private var dispersion = 0.16f
    private var blurRadiusPx = dp(1.4f)
    private var elastic = false
    private var touchEffect = false
    private var isBound = false

    init {
        setWillNotDraw(false)
        setBackgroundColor(Color.TRANSPARENT)
        clipChildren = true
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_NO
        isClickable = false
        isFocusable = false
        fallbackFill.color = Color.argb(
            if (darkTheme) 74 else 92,
            Color.red(fallbackTint),
            Color.green(fallbackTint),
            Color.blue(fallbackTint),
        )
        fallbackStroke.color = Color.argb(
            if (darkTheme) 86 else 60,
            Color.red(palette.title),
            Color.green(palette.title),
            Color.blue(palette.title),
        )
        glassView?.let {
            it.setBackgroundColor(Color.TRANSPARENT)
            it.importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_NO
            addView(it, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        }
        applyGlassParameters()
    }

    fun configure(
        radiusPx: Float,
        refractionHeightPx: Float = dp(28f),
        refractionOffsetPx: Float = dp(42f),
        tintAlpha: Float = if (darkTheme) 0.18f else 0.24f,
        dispersion: Float = 0.16f,
        blurRadiusPx: Float = dp(1.4f),
        elastic: Boolean = false,
        touchEffect: Boolean = false,
    ) {
        cornerRadiusPx = radiusPx.coerceAtLeast(0f)
        this.refractionHeightPx = refractionHeightPx.coerceIn(dp(12f), dp(50f))
        this.refractionOffsetPx = refractionOffsetPx.coerceIn(dp(20f), dp(120f))
        this.tintAlpha = tintAlpha.coerceIn(0f, 1f)
        this.dispersion = dispersion.coerceIn(0f, 1f)
        this.blurRadiusPx = blurRadiusPx.coerceAtLeast(0f)
        this.elastic = elastic
        this.touchEffect = touchEffect
        applyGlassParameters()
        invalidate()
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        (backdropHost as? LiquidGlassExclusionHost)?.registerLiquidGlassView(this)
        post {
            bindBackdrop()
            applyGlassParameters()
        }
    }

    override fun onDetachedFromWindow() {
        (backdropHost as? LiquidGlassExclusionHost)?.unregisterLiquidGlassView(this)
        isBound = false
        super.onDetachedFromWindow()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (width <= 0 || height <= 0) return
        // Keep the fallback visible on API 29-32 and if a software/vendor
        // renderer cannot bind the real source. On API 33+ the library draws
        // the refracted surface above this quiet base.
        if (!fullGlassSupported || !isBound || !canvas.isHardwareAccelerated) {
            val radius = min(cornerRadiusPx, min(width, height) / 2f)
            val rect = RectF(0f, 0f, width.toFloat(), height.toFloat())
            canvas.drawRoundRect(rect, radius, radius, fallbackFill)
            canvas.drawRoundRect(rect, radius, radius, fallbackStroke)
        }
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        applyGlassParameters()
        if (!isBound) post { bindBackdrop() }
    }

    override fun onWindowVisibilityChanged(visibility: Int) {
        super.onWindowVisibilityChanged(visibility)
        if (visibility == View.VISIBLE && !isBound) post { bindBackdrop() }
    }

    private fun bindBackdrop() {
        val source = backdropHost ?: return
        val view = glassView ?: return
        if (isBound || !isAttachedToWindow) return
        runCatching {
            view.bind(source)
            isBound = true
        }.onFailure {
            isBound = false
        }
        invalidate()
    }

    private fun applyGlassParameters() {
        val view = glassView ?: return
        view.setCornerRadius(cornerRadiusPx)
        // QmDeve's refraction setters accept dp and perform the density
        // conversion internally; corner radius is the only geometry setter
        // that consumes the already-resolved pixel value.
        view.setRefractionHeight(refractionHeightPx / density)
        view.setRefractionOffset(refractionOffsetPx / density)
        view.setTintColorRed(Color.red(fallbackTint) / 255f)
        view.setTintColorGreen(Color.green(fallbackTint) / 255f)
        view.setTintColorBlue(Color.blue(fallbackTint) / 255f)
        view.setTintAlpha(tintAlpha)
        view.setDispersion(dispersion)
        view.setBlurRadius(blurRadiusPx)
        view.setDraggableEnabled(false)
        view.setElasticEnabled(elastic)
        view.setTouchEffectEnabled(touchEffect)
    }

    private fun dp(value: Float): Float = value * density
}
