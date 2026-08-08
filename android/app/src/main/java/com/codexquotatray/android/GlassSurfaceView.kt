package com.codexquotatray.android

import android.annotation.SuppressLint
import android.graphics.Bitmap
import android.graphics.BitmapShader
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PorterDuff
import android.graphics.RectF
import android.graphics.Shader
import android.os.Build
import android.view.View
import android.view.ViewGroup

/**
 * A small reusable glass surface. It captures a clipped backdrop once per
 * layout/scroll refresh and only updates shader geometry during animations.
 * The same AGSL renderer is shared with the bottom navigation capsule.
 */
@SuppressLint("NewApi")
internal class GlassSurfaceView(
    context: android.content.Context,
    private val backdropHost: ViewGroup,
    private val palette: ThemePalette,
    private val darkTheme: Boolean,
    private val fallbackTint: Int = palette.surface,
) : View(context) {
    private val overscanPx = (12f * resources.displayMetrics.density).toInt().coerceAtLeast(6)
    private val fallbackFill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val fallbackStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = resources.displayMetrics.density.coerceAtLeast(1f)
    }
    private var capture: Bitmap? = null
    private var captureCanvas: Canvas? = null
    private var hasBackdrop = false
    private var geometry = GlassGeometry(0f, 0f, 0f, 0f, 0f, 0f, 0f)
    private var captureExclusion: View? = null
    private var renderer: AgslGlassRenderer? = if (Build.VERSION.SDK_INT >= 33) {
        runCatching { AgslGlassRenderer(palette, tintColor = fallbackTint) }.getOrNull()
    } else {
        null
    }
    private var scrollListener: android.view.ViewTreeObserver.OnScrollChangedListener? = null
    private var lastBackdropRefreshNanos = 0L
    private var backdropRefreshPosted = false

    init {
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_NO
        isClickable = false
        isFocusable = false
        fallbackFill.color = Color.argb(
            if (darkTheme) 116 else 148,
            Color.red(fallbackTint),
            Color.green(fallbackTint),
            Color.blue(fallbackTint),
        )
        fallbackStroke.color = Color.argb(
            if (darkTheme) 82 else 58,
            Color.red(palette.title),
            Color.green(palette.title),
            Color.blue(palette.title),
        )
    }

    fun setGeometry(next: GlassGeometry) {
        geometry = next
        updateUniforms()
        invalidate()
    }

    fun setCaptureExclusion(view: View) {
        captureExclusion = view
    }

    fun setRoundedRect(
        centerX: Float,
        centerY: Float,
        halfWidth: Float,
        halfHeight: Float,
        radius: Float,
        displacementPx: Float,
        surfaceAlpha: Float,
    ) = setGeometry(
        GlassGeometry(
            centerX,
            centerY,
            halfWidth,
            halfHeight,
            radius,
            displacementPx,
            surfaceAlpha,
        ),
    )

    fun refreshBackdrop() {
        backdropRefreshPosted = false
        if (width <= 0 || height <= 0 || backdropHost.width <= 0 || backdropHost.height <= 0) return
        val bitmapWidth = width + overscanPx * 2
        val bitmapHeight = height + overscanPx * 2
        if (capture == null || capture!!.width != bitmapWidth || capture!!.height != bitmapHeight) {
            capture = Bitmap.createBitmap(bitmapWidth, bitmapHeight, Bitmap.Config.ARGB_8888)
            captureCanvas = Canvas(capture!!)
        }
        val bitmap = capture ?: return
        val canvas = captureCanvas ?: return
        canvas.drawColor(Color.TRANSPARENT, PorterDuff.Mode.CLEAR)

        val hostLocation = IntArray(2)
        val viewLocation = IntArray(2)
        backdropHost.getLocationInWindow(hostLocation)
        getLocationInWindow(viewLocation)
        val cropLeft = viewLocation[0] - hostLocation[0] - overscanPx
        val cropTop = viewLocation[1] - hostLocation[1] - overscanPx
        val exclusion = captureExclusion ?: this
        val previousVisibility = exclusion.visibility
        // Do not include this control's own pixels in the backdrop snapshot.
        exclusion.visibility = INVISIBLE
        canvas.save()
        try {
            canvas.translate(-cropLeft.toFloat(), -cropTop.toFloat())
            backdropHost.draw(canvas)
        } finally {
            canvas.restore()
            exclusion.visibility = previousVisibility
        }

        runCatching {
            renderer?.setBackdrop(BitmapShader(bitmap, Shader.TileMode.CLAMP, Shader.TileMode.CLAMP))
            hasBackdrop = renderer != null
        }.onFailure {
            renderer = null
            hasBackdrop = false
        }
        updateUniforms()
        invalidate()
        lastBackdropRefreshNanos = System.nanoTime()
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        scrollListener = android.view.ViewTreeObserver.OnScrollChangedListener { scheduleBackdropRefresh() }
        backdropHost.viewTreeObserver.addOnScrollChangedListener(scrollListener)
        post { refreshBackdrop() }
    }

    override fun onDetachedFromWindow() {
        scrollListener?.let { backdropHost.viewTreeObserver.removeOnScrollChangedListener(it) }
        scrollListener = null
        super.onDetachedFromWindow()
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        updateUniforms()
        post { refreshBackdrop() }
    }

    private fun scheduleBackdropRefresh() {
        val now = System.nanoTime()
        val intervalNanos = 50_000_000L
        if (now - lastBackdropRefreshNanos >= intervalNanos) {
            refreshBackdrop()
            lastBackdropRefreshNanos = now
            return
        }
        if (backdropRefreshPosted) return
        backdropRefreshPosted = true
        val delayMillis = ((intervalNanos - (now - lastBackdropRefreshNanos)) / 1_000_000L)
            .coerceAtLeast(1L)
        postDelayed({
            refreshBackdrop()
            lastBackdropRefreshNanos = System.nanoTime()
        }, delayMillis)
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (width <= 0 || height <= 0) return
        if (renderer == null || !hasBackdrop || !canvas.isHardwareAccelerated) {
            drawFallback(canvas)
            return
        }
        runCatching {
            if (renderer?.draw(canvas, width.toFloat(), height.toFloat()) != true) drawFallback(canvas)
        }.onFailure {
            renderer = null
            drawFallback(canvas)
        }
    }

    private fun updateUniforms() {
        if (width <= 0 || height <= 0) return
        renderer?.setGeometry(width.toFloat(), height.toFloat(), overscanPx.toFloat(), geometry)
    }

    private fun drawFallback(canvas: Canvas) {
        val rect = RectF(
            geometry.centerX - geometry.halfWidth,
            geometry.centerY - geometry.halfHeight,
            geometry.centerX + geometry.halfWidth,
            geometry.centerY + geometry.halfHeight,
        )
        val radius = geometry.radius.coerceAtMost(minOf(rect.width(), rect.height()) / 2f)
        canvas.drawRoundRect(rect, radius, radius, fallbackFill)
        canvas.drawRoundRect(rect, radius, radius, fallbackStroke)
    }
}
