package com.codexquotatray.android

import android.annotation.SuppressLint
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RuntimeShader
import android.graphics.Shader

/** Geometry passed to the shared rounded-rect/circle glass shader. */
internal data class GlassGeometry(
    val centerX: Float,
    val centerY: Float,
    val halfWidth: Float,
    val halfHeight: Float,
    val radius: Float,
    val displacementPx: Float,
    val surfaceAlpha: Float,
)

/**
 * Shared API 33+ AGSL renderer used by the navigation capsule and small glass
 * controls. It owns one RuntimeShader and never allocates a bitmap while a
 * control is being dragged or animated.
 */
@SuppressLint("NewApi")
internal class AgslGlassRenderer(
    palette: ThemePalette,
    tintColor: Int = palette.surface,
    accentColor: Int = palette.accent,
) {
    private val shader = RuntimeShader(AGSL_GLASS_SHADER_SOURCE)
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.shader = this@AgslGlassRenderer.shader }
    private var input: Shader? = null
    private val tintR = Color.red(tintColor) / 255f
    private val tintG = Color.green(tintColor) / 255f
    private val tintB = Color.blue(tintColor) / 255f
    private val accentR = Color.red(accentColor) / 255f
    private val accentG = Color.green(accentColor) / 255f
    private val accentB = Color.blue(accentColor) / 255f

    fun setBackdrop(backdrop: Shader) {
        input = backdrop
        shader.setInputShader("content", backdrop)
    }

    fun setGeometry(width: Float, height: Float, overscan: Float, geometry: GlassGeometry) {
        shader.setFloatUniform("resolution", width + overscan * 2f, height + overscan * 2f)
        shader.setFloatUniform("shapeCenter", geometry.centerX, geometry.centerY)
        shader.setFloatUniform("shapeHalfSize", geometry.halfWidth, geometry.halfHeight)
        shader.setFloatUniform("shapeRadius", geometry.radius)
        shader.setFloatUniform("overscan", overscan)
        shader.setFloatUniform("displacementPx", geometry.displacementPx)
        shader.setFloatUniform("surfaceAlpha", geometry.surfaceAlpha)
        shader.setFloatUniform("tint", tintR, tintG, tintB)
        shader.setFloatUniform("accent", accentR, accentG, accentB)
    }

    fun draw(canvas: Canvas, width: Float, height: Float): Boolean {
        if (input == null) return false
        canvas.drawRect(0f, 0f, width, height, paint)
        return true
    }
}

/** One shader source for capsule, circle and rounded-rect surfaces. */
private const val AGSL_GLASS_SHADER_SOURCE = """
    uniform shader content;
    uniform vec2 resolution;
    uniform vec2 shapeCenter;
    uniform vec2 shapeHalfSize;
    uniform float shapeRadius;
    uniform float overscan;
    uniform float displacementPx;
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

        // The SDF gradient is the real normal for rounded corners and long
        // capsule sides; a point-to-centre radial vector is not.
        float epsilon = 1.0;
        float dx = roundedSdf(point + vec2(epsilon, 0.0), shapeCenter, shapeHalfSize, shapeRadius)
            - roundedSdf(point - vec2(epsilon, 0.0), shapeCenter, shapeHalfSize, shapeRadius);
        float dy = roundedSdf(point + vec2(0.0, epsilon), shapeCenter, shapeHalfSize, shapeRadius)
            - roundedSdf(point - vec2(0.0, epsilon), shapeCenter, shapeHalfSize, shapeRadius);
        vec2 gradient = vec2(dx, dy);
        vec2 normal = gradient / max(length(gradient), 0.001);
        float inner = 1.0 - smoothstep(-shapeRadius * 0.25, shapeRadius * 0.85, sdfDistance);
        float edgeBand = max(shapeRadius * 0.22, 2.0);
        float edgeFactor = smoothstep(-edgeBand, 0.0, sdfDistance);
        vec2 samplePoint = point + normal * displacementPx * edgeFactor;
        vec2 uv = clamp(samplePoint + vec2(overscan, overscan),
            vec2(0.0, 0.0), resolution - vec2(1.0, 1.0));

        vec4 base = content.eval(uv);
        float chroma = 0.18;
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
