package com.codexquotatray.android

import android.annotation.SuppressLint
import android.graphics.Bitmap
import android.graphics.BitmapShader
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PorterDuff
import android.graphics.RectF
import android.graphics.RuntimeShader
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
        runCatching { Api33Engine(palette, darkTheme) }.getOrNull()
    } else {
        null
    }

    init {
        importantForAccessibility = IMPORTANT_FOR_ACCESSIBILITY_NO
        isClickable = false
        isFocusable = false
        setLayerType(View.LAYER_TYPE_HARDWARE, null)
        fallbackOuterPaint.color = Color.argb(
            if (darkTheme) 50 else 122,
            Color.red(Color.WHITE),
            Color.green(Color.WHITE),
            Color.blue(Color.WHITE),
        )
        fallbackLensPaint.color = Color.argb(
            if (darkTheme) 66 else 42,
            Color.red(palette.accent),
            Color.green(palette.accent),
            Color.blue(palette.accent),
        )
        fallbackOuterStroke.color = Color.argb(
            if (darkTheme) 48 else 34,
            Color.red(palette.title),
            Color.green(palette.title),
            Color.blue(palette.title),
        )
        fallbackLensStroke.color = Color.argb(
            if (darkTheme) 78 else 56,
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
        private val palette: ThemePalette,
        darkTheme: Boolean,
    ) : Engine {
        private val outerShader = RuntimeShader(SHADER_SOURCE)
        private val lensShader = RuntimeShader(SHADER_SOURCE)
        private val outerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { shader = outerShader }
        private val lensPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { shader = lensShader }
        private var input: Shader? = null
        private var width = 0f
        private var height = 0f
        private var inset = 0f
        private var progress = 0f
        private var stretch = 0f
        private var overscan = 0f
        private val tintR = Color.red(palette.surface) / 255f
        private val tintG = Color.green(palette.surface) / 255f
        private val tintB = Color.blue(palette.surface) / 255f
        private val accentR = Color.red(palette.accent) / 255f
        private val accentG = Color.green(palette.accent) / 255f
        private val accentB = Color.blue(palette.accent) / 255f
        private val outerAlpha = if (darkTheme) 0.18f else 0.34f

        override fun setBackdrop(shader: Shader) {
            input = shader
            outerShader.setInputShader("content", shader)
            lensShader.setInputShader("content", shader)
        }

        override fun setGeometry(
            width: Float,
            height: Float,
            inset: Float,
            progress: Float,
            stretch: Float,
            overscan: Float,
        ) {
            this.width = width
            this.height = height
            this.inset = inset
            this.progress = progress
            this.stretch = stretch
            this.overscan = overscan
            val innerWidth = (width - inset * 2f).coerceAtLeast(0f)
            val lensWidth = innerWidth / 2f
            val lensCenterX = inset + lensWidth / 2f + lensWidth * progress
            val innerHeight = (height - inset * 2f).coerceAtLeast(0f)
            setShape(
                outerShader,
                width / 2f,
                height / 2f,
                width / 2f,
                height / 2f,
                height / 2f,
                0.22f,
                outerAlpha,
            )
            setShape(
                lensShader,
                lensCenterX,
                height / 2f,
                lensWidth / 2f * (1f + stretch * 0.055f),
                innerHeight / 2f * (1f - stretch * 0.028f),
                innerHeight / 2f,
                0.62f,
                0.68f,
            )
        }

        override fun draw(canvas: Canvas, width: Float, height: Float) {
            if (input == null) return
            canvas.drawRect(0f, 0f, width, height, outerPaint)
            canvas.drawRect(0f, 0f, width, height, lensPaint)
        }

        private fun setShape(
            shader: RuntimeShader,
            centerX: Float,
            centerY: Float,
            halfWidth: Float,
            halfHeight: Float,
            radius: Float,
            strength: Float,
            alpha: Float,
        ) {
            shader.setFloatUniform("resolution", width + overscan * 2f, height + overscan * 2f)
            shader.setFloatUniform("shapeCenter", centerX, centerY)
            shader.setFloatUniform("shapeHalfSize", halfWidth, halfHeight)
            shader.setFloatUniform("shapeRadius", radius)
            shader.setFloatUniform("overscan", overscan)
            shader.setFloatUniform("refractionStrength", strength * (1f + stretch * 0.32f))
            shader.setFloatUniform("surfaceAlpha", alpha)
            shader.setFloatUniform("tint", tintR, tintG, tintB)
            shader.setFloatUniform("accent", accentR, accentG, accentB)
        }
    }

    private companion object {
        private const val TAG = "CodexQuotaGlass"

        private const val SHADER_SOURCE = """
            uniform shader content;
            uniform vec2 resolution;
            uniform vec2 shapeCenter;
            uniform vec2 shapeHalfSize;
            uniform float shapeRadius;
            uniform float overscan;
            uniform float refractionStrength;
            uniform float surfaceAlpha;
            uniform vec3 tint;
            uniform vec3 accent;

            float roundedSdf(vec2 point, vec2 center, vec2 halfSize, float radius) {
                vec2 q = abs(point - center) - halfSize + vec2(radius, radius);
                return length(max(q, vec2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - radius;
            }

            vec4 main(vec2 point) {
                float sdfDistance = roundedSdf(point, shapeCenter, shapeHalfSize, shapeRadius);
                float mask = 1.0 - smoothstep(0.0, 1.8, sdfDistance);
                if (mask <= 0.0) return vec4(0.0);

                vec2 radial = point - shapeCenter;
                float radialLength = max(length(radial), 0.001);
                vec2 normal = radial / radialLength;
                float inner = 1.0 - smoothstep(-shapeRadius * 0.25, shapeRadius * 0.85, sdfDistance);
                vec2 magnifiedPoint = shapeCenter + radial * (1.0 - inner * 0.016);
                vec2 samplePoint = magnifiedPoint + normal * refractionStrength * (0.35 + inner * 0.65);
                vec2 uv = clamp(samplePoint + vec2(overscan, overscan),
                    vec2(0.0, 0.0), resolution - vec2(1.0, 1.0));

                vec4 base = content.eval(uv);
                float chroma = 0.45;
                vec4 redSample = content.eval(clamp(uv + vec2(chroma, 0.0),
                    vec2(0.0, 0.0), resolution - vec2(1.0, 1.0)));
                vec4 blueSample = content.eval(clamp(uv - vec2(chroma, 0.0),
                    vec2(0.0, 0.0), resolution - vec2(1.0, 1.0)));
                vec3 refracted = vec3(redSample.r, base.g, blueSample.b);

                float edge = 1.0 - smoothstep(-2.0, 3.0, sdfDistance);
                float fresnel = pow(clamp(1.0 - inner, 0.0, 1.0), 2.0);
                vec2 lightDirection = normalize(vec2(-0.42, -0.86));
                float specular = pow(max(dot(normal, lightDirection), 0.0), 72.0);
                vec3 color = mix(refracted, refracted * 1.025 + tint * 0.16, 0.22);
                color += accent * (fresnel * 0.08);
                color += vec3(1.0, 1.0, 1.0) * (specular * 0.12 + edge * 0.035);
                float opacity = mask * surfaceAlpha;
                return vec4(clamp(color, vec3(0.0), vec3(1.0)) * opacity, opacity);
            }
        """
    }
}
