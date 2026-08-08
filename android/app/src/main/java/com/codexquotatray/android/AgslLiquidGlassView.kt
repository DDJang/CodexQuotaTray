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
import android.content.pm.ApplicationInfo
import android.os.Build
import android.util.Log
import android.view.View
import android.view.ViewGroup
import kotlin.math.max

/**
 * A small, ordinary View renderer for the navigation capsule.
 *
 * API 33+ uses AGSL over a reused bitmap strip. Older devices use a clipped,
 * translucent native fallback; neither path uses a separate compositor surface.
 */
@SuppressLint("NewApi")
internal class AgslLiquidGlassView(
    context: android.content.Context,
    private val backdropHost: ViewGroup,
    private val palette: ThemePalette,
    private val insetPx: Float,
    private val darkTheme: Boolean,
) : View(context) {
    private val overscanPx = (16f * resources.displayMetrics.density).toInt().coerceAtLeast(8)
    private val fallbackOuterPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val fallbackLensPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val fallbackOuterStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = max(1f, resources.displayMetrics.density)
    }
    private val fallbackLensStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = max(1f, resources.displayMetrics.density)
    }
    private var capture: Bitmap? = null
    private var captureCanvas: Canvas? = null
    private var hasBackdrop = false
    private var diagnosticsLogged = false
    private val diagnosticsEnabled =
        (context.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0
    private var progress = 0f
    private var dragStretch = 0f
    private var engine: Engine? = if (Build.VERSION.SDK_INT >= 33) {
        runCatching { Api33Engine(palette, darkTheme, resources.displayMetrics.density) }.getOrNull()
    } else {
        null
    }

    init {
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_NO
        isClickable = false
        isFocusable = false
        fallbackOuterPaint.color = Color.argb(
            if (darkTheme) 34 else 58,
            Color.red(Color.WHITE),
            Color.green(Color.WHITE),
            Color.blue(Color.WHITE),
        )
        fallbackLensPaint.color = Color.argb(
            if (darkTheme) 46 else 32,
            Color.red(palette.accent),
            Color.green(palette.accent),
            Color.blue(palette.accent),
        )
        fallbackOuterStroke.color = Color.argb(
            if (darkTheme) 44 else 34,
            Color.red(palette.title),
            Color.green(palette.title),
            Color.blue(palette.title),
        )
        fallbackLensStroke.color = Color.argb(
            if (darkTheme) 64 else 48,
            Color.red(palette.accent),
            Color.green(palette.accent),
            Color.blue(palette.accent),
        )
    }

    fun setLensProgress(value: Float, stretching: Float) {
        progress = value.coerceIn(0f, 1f)
        dragStretch = stretching.coerceIn(0f, 1f)
        runCatching { updateUniforms() }.onFailure {
            engine = null
            hasBackdrop = false
        }
        invalidate()
    }

    /** Captures only the capsule-sized strip plus overscan; the bitmap is reused. */
    fun refreshBackdrop() {
        if (width <= 0 || height <= 0 || backdropHost.width <= 0 || backdropHost.height <= 0) {
            return
        }
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
        canvas.save()
        canvas.translate(-cropLeft.toFloat(), -cropTop.toFloat())
        backdropHost.draw(canvas)
        canvas.restore()

        runCatching {
            engine?.setBackdrop(BitmapShader(bitmap, Shader.TileMode.CLAMP, Shader.TileMode.CLAMP))
            hasBackdrop = engine != null
        }.onFailure {
            engine = null
            hasBackdrop = false
        }
        if (diagnosticsEnabled && !diagnosticsLogged) {
            Log.d(TAG, "renderer=${if (engine != null) "AGSL" else "fallback"} api=${Build.VERSION.SDK_INT} " +
                "backdrop=${bitmap.width}x${bitmap.height}")
            diagnosticsLogged = true
        }
        updateUniforms()
        invalidate()
    }

    override fun onSizeChanged(width: Int, height: Int, oldWidth: Int, oldHeight: Int) {
        super.onSizeChanged(width, height, oldWidth, oldHeight)
        updateUniforms()
        post { refreshBackdrop() }
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (width <= 0 || height <= 0) return
        if (engine == null || !hasBackdrop || !canvas.isHardwareAccelerated) {
            drawFallback(canvas)
            return
        }
        runCatching {
            engine?.draw(canvas, width.toFloat(), height.toFloat())
        }.onFailure {
            // Some vendor renderers reject RuntimeShader at runtime. Keep the
            // visual fallback instead of allowing a black or missing surface.
            engine = null
            drawFallback(canvas)
        }
    }

    private fun updateUniforms() {
        engine?.setGeometry(
            width.toFloat(),
            height.toFloat(),
            insetPx,
            progress,
            dragStretch,
            overscanPx.toFloat(),
        )
    }

    private fun drawFallback(canvas: Canvas) {
        val outerRadius = height / 2f
        val outer = RectF(0f, 0f, width.toFloat(), height.toFloat())
        canvas.drawRoundRect(outer, outerRadius, outerRadius, fallbackOuterPaint)
        canvas.drawRoundRect(outer, outerRadius, outerRadius, fallbackOuterStroke)

        val innerWidth = (width - insetPx * 2f).coerceAtLeast(0f)
        val lensWidth = innerWidth / 2f
        val centerX = insetPx + lensWidth / 2f + (lensWidth * progress)
        val halfHeight = ((height - insetPx * 2f) / 2f).coerceAtLeast(0f)
        val lens = RectF(
            centerX - lensWidth / 2f,
            insetPx,
            centerX + lensWidth / 2f,
            height - insetPx,
        )
        canvas.save()
        canvas.scale(1f + dragStretch * 0.045f, 1f - dragStretch * 0.025f, centerX, height / 2f)
        canvas.drawRoundRect(lens, halfHeight, halfHeight, fallbackLensPaint)
        canvas.drawRoundRect(lens, halfHeight, halfHeight, fallbackLensStroke)
        canvas.restore()
    }

    private interface Engine {
        fun setBackdrop(shader: Shader)
        fun setGeometry(
            width: Float,
            height: Float,
            inset: Float,
            progress: Float,
            stretch: Float,
            overscan: Float,
        )

        fun draw(canvas: Canvas, width: Float, height: Float)
    }

    private class Api33Engine(
        palette: ThemePalette,
        darkTheme: Boolean,
        private val density: Float,
    ) : Engine {
        private val outerRenderer = AgslGlassRenderer(palette)
        private val lensRenderer = AgslGlassRenderer(palette)
        private val outerAlpha = if (darkTheme) 0.12f else 0.16f

        override fun setBackdrop(shader: Shader) {
            outerRenderer.setBackdrop(shader)
            lensRenderer.setBackdrop(shader)
        }

        override fun setGeometry(
            width: Float,
            height: Float,
            inset: Float,
            progress: Float,
            stretch: Float,
            overscan: Float,
        ) {
            val innerWidth = (width - inset * 2f).coerceAtLeast(0f)
            val lensWidth = innerWidth / 2f
            val lensCenterX = inset + lensWidth / 2f + lensWidth * progress
            val innerHeight = (height - inset * 2f).coerceAtLeast(0f)
            outerRenderer.setGeometry(
                width,
                height,
                overscan,
                GlassGeometry(
                    centerX = width / 2f,
                    centerY = height / 2f,
                    halfWidth = width / 2f,
                    halfHeight = height / 2f,
                    radius = height / 2f,
                    displacementPx = 3f * density * (1f + stretch * 0.32f),
                    surfaceAlpha = outerAlpha,
                    blurPx = 1.1f * density,
                ),
            )
            lensRenderer.setGeometry(
                width,
                height,
                overscan,
                GlassGeometry(
                    centerX = lensCenterX,
                    centerY = height / 2f,
                    halfWidth = lensWidth / 2f * (1f + stretch * 0.055f),
                    halfHeight = innerHeight / 2f * (1f - stretch * 0.028f),
                    radius = innerHeight / 2f,
                    displacementPx = 7f * density * (1f + stretch * 0.32f),
                    surfaceAlpha = 0.26f,
                    blurPx = 0.9f * density,
                ),
            )
        }

        override fun draw(canvas: Canvas, width: Float, height: Float) {
            outerRenderer.draw(canvas, width, height)
            lensRenderer.draw(canvas, width, height)
        }
    }

    private companion object {
        private const val TAG = "CodexQuotaGlass"
    }
}
