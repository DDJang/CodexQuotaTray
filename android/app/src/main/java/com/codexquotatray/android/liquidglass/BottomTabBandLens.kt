// Adapted from Kyant0/backdrop 2.0.0's rounded-rectangle refraction shader.
// The local correction is an investigation-only Android fixture path.
// Apache License 2.0; see android/app/src/main/assets/licenses/AndroidLiquidGlass-APACHE-2.0.txt.
package com.codexquotatray.android.liquidglass

import androidx.compose.foundation.shape.AbsoluteRoundedCornerShape
import androidx.compose.foundation.shape.CornerBasedShape
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.util.fastCoerceAtMost
import com.kyant.backdrop.BackdropEffectScope
import com.kyant.backdrop.effects.runtimeShaderEffect
import com.kyant.backdrop.isRenderEffectSupported
import com.kyant.backdrop.isRuntimeShaderSupported
import com.kyant.shapes.RoundedRectangularShape
import org.intellij.lang.annotations.Language

/** Debug diagnostics for the single-pass Option D shader. */
enum class BottomTabBandLensDiagnostic {
    NONE,
    REFRACTION_REFERENCE,
    SIGNED_DIFFERENCE,
    SOURCE,
}

internal fun normalizeBottomTabBandLensStrength(strength: Float): Float =
    if (strength.isFinite()) strength.coerceIn(0f, 1f) else 0f

internal fun BackdropEffectScope.applyBottomTabBandLens(
    refractionHeight: Float,
    refractionAmount: Float,
    strength: Float,
    accentColor: Color,
    diagnostic: BottomTabBandLensDiagnostic,
): Boolean {
    if (!isRuntimeShaderSupported() || !isRenderEffectSupported()) return false
    if (!refractionHeight.isFinite() || refractionHeight <= 0f) return false
    if (!refractionAmount.isFinite() || refractionAmount <= 0f) return false
    val normalizedStrength = normalizeBottomTabBandLensStrength(strength)
    val cornerRadii = bottomTabBandCornerRadii() ?: return false
    val nextPadding = (padding - refractionHeight).coerceAtLeast(0f)
    val previousRenderEffect = renderEffect

    return try {
        runtimeShaderEffect(
            key = BOTTOM_TAB_BAND_LENS_SHADER_KEY,
            shaderString = BOTTOM_TAB_BAND_LENS_SHADER,
            uniformShaderName = "content",
        ) {
            setFloatUniform("size", size.width, size.height)
            setFloatUniform("offset", -nextPadding, -nextPadding)
            setFloatUniform("cornerRadii", cornerRadii)
            setFloatUniform("refractionHeight", refractionHeight)
            // Kyant's lens passes the amount with the opposite sign.
            setFloatUniform("refractionAmount", -refractionAmount)
            setFloatUniform("strength", normalizedStrength)
            setFloatUniform("diagnosticMode", diagnostic.shaderValue())
            setColorUniform("accentColor", accentColor)
        }
        padding = nextPadding
        true
    } catch (_: RuntimeException) {
        // Leave the effect chain and padding untouched so the caller can use the original lens.
        renderEffect = previousRenderEffect
        false
    }
}

private fun BottomTabBandLensDiagnostic.shaderValue(): Float =
    when (this) {
        BottomTabBandLensDiagnostic.NONE -> 0f
        BottomTabBandLensDiagnostic.REFRACTION_REFERENCE -> 1f
        BottomTabBandLensDiagnostic.SIGNED_DIFFERENCE -> 2f
        BottomTabBandLensDiagnostic.SOURCE -> 3f
    }

private fun BackdropEffectScope.bottomTabBandCornerRadii(): FloatArray? =
    when (val currentShape = shape) {
        is RoundedRectangularShape -> {
            val corners = currentShape.corners(size, layoutDirection, this)
            floatArrayOf(
                corners.topLeft,
                corners.topRight,
                corners.bottomRight,
                corners.bottomLeft,
            )
        }

        is AbsoluteRoundedCornerShape -> {
            val currentSize = size
            val maxRadius = currentSize.minDimension / 2f
            val topLeft = currentShape.topStart.toPx(currentSize, this)
            val topRight = currentShape.topEnd.toPx(currentSize, this)
            val bottomRight = currentShape.bottomEnd.toPx(currentSize, this)
            val bottomLeft = currentShape.bottomStart.toPx(currentSize, this)
            floatArrayOf(
                topLeft.fastCoerceAtMost(maxRadius),
                topRight.fastCoerceAtMost(maxRadius),
                bottomRight.fastCoerceAtMost(maxRadius),
                bottomLeft.fastCoerceAtMost(maxRadius),
            )
        }

        is CornerBasedShape -> {
            val currentSize = size
            val maxRadius = currentSize.minDimension / 2f
            val isLtr = layoutDirection == LayoutDirection.Ltr
            val topLeft =
                if (isLtr) currentShape.topStart.toPx(currentSize, this)
                else currentShape.topEnd.toPx(currentSize, this)
            val topRight =
                if (isLtr) currentShape.topEnd.toPx(currentSize, this)
                else currentShape.topStart.toPx(currentSize, this)
            val bottomRight =
                if (isLtr) currentShape.bottomEnd.toPx(currentSize, this)
                else currentShape.bottomStart.toPx(currentSize, this)
            val bottomLeft =
                if (isLtr) currentShape.bottomStart.toPx(currentSize, this)
                else currentShape.bottomEnd.toPx(currentSize, this)
            floatArrayOf(
                topLeft.fastCoerceAtMost(maxRadius),
                topRight.fastCoerceAtMost(maxRadius),
                bottomRight.fastCoerceAtMost(maxRadius),
                bottomLeft.fastCoerceAtMost(maxRadius),
            )
        }

        else -> null
    }

private const val BOTTOM_TAB_BAND_LENS_SHADER_KEY = "BottomTabBandLens"

@Language("AGSL")
private val BOTTOM_TAB_BAND_LENS_SHADER = """
uniform shader content;

uniform float2 size;
uniform float2 offset;
uniform float4 cornerRadii;
uniform float refractionHeight;
uniform float refractionAmount;
uniform float strength;
uniform float diagnosticMode;
layout(color) uniform half4 accentColor;

float radiusAt(float2 coord, float4 radii) {
    if (coord.x >= 0.0) {
        if (coord.y <= 0.0) return radii.y;
        else return radii.z;
    } else {
        if (coord.y <= 0.0) return radii.x;
        else return radii.w;
    }
}

float sdRoundedRect(float2 coord, float2 halfSize, float radius) {
    float2 cornerCoord = abs(coord) - (halfSize - float2(radius));
    float outside = length(max(cornerCoord, 0.0)) - radius;
    float inside = min(max(cornerCoord.x, cornerCoord.y), 0.0);
    return outside + inside;
}

float2 gradSdRoundedRect(float2 coord, float2 halfSize, float radius) {
    float2 cornerCoord = abs(coord) - (halfSize - float2(radius));
    if (cornerCoord.x >= 0.0 || cornerCoord.y >= 0.0) {
        return sign(coord) * normalize(max(cornerCoord, 0.0));
    } else {
        float gradX = step(cornerCoord.y, cornerCoord.x);
        return sign(coord) * float2(gradX, 1.0 - gradX);
    }
}

float circleMap(float x) {
    return 1.0 - sqrt(1.0 - x * x);
}

float safeCorrectionScale(float3 value, float alpha, float3 delta) {
    const float epsilon = 0.0001;
    float safeScale = 1.0;
    if (delta.r > epsilon) safeScale = min(safeScale, max((alpha - value.r) / delta.r, 0.0));
    else if (delta.r < -epsilon) safeScale = min(safeScale, max(value.r / -delta.r, 0.0));
    if (delta.g > epsilon) safeScale = min(safeScale, max((alpha - value.g) / delta.g, 0.0));
    else if (delta.g < -epsilon) safeScale = min(safeScale, max(value.g / -delta.g, 0.0));
    if (delta.b > epsilon) safeScale = min(safeScale, max((alpha - value.b) / delta.b, 0.0));
    else if (delta.b < -epsilon) safeScale = min(safeScale, max(value.b / -delta.b, 0.0));
    return safeScale;
}

half4 main(float2 coord) {
    const float epsilon = 0.0001;
    float2 halfSize = size * 0.5;
    float2 centeredCoord = (coord + offset) - halfSize;
    float radius = radiusAt(coord, cornerRadii);

    float sd = sdRoundedRect(centeredCoord, halfSize, radius);
    if (-sd >= refractionHeight) {
        if (diagnosticMode > 1.5 && diagnosticMode < 2.5) return half4(0.5);
        return content.eval(coord);
    }
    if (diagnosticMode > 2.5) return content.eval(coord);
    sd = min(sd, 0.0);

    float d = circleMap(1.0 - -sd / refractionHeight) * refractionAmount;
    float gradRadius = min(radius * 1.5, min(halfSize.x, halfSize.y));
    float2 grad = normalize(gradSdRoundedRect(centeredCoord, halfSize, gradRadius));
    float2 refractedCoord = coord + d * grad;

    half4 center = content.eval(refractedCoord);
    if (diagnosticMode > 0.5 && diagnosticMode < 1.5) return center;

    float dispersionIntensity = (centeredCoord.x * centeredCoord.y) / (halfSize.x * halfSize.y);
    float2 dispersedCoord = d * grad * dispersionIntensity;

    half4 color = half4(0.0);

    half4 red = content.eval(refractedCoord + dispersedCoord);
    color.r += red.r / 3.5;
    color.a += red.a / 7.0;

    half4 orange = content.eval(refractedCoord + dispersedCoord * (2.0 / 3.0));
    color.r += orange.r / 3.5;
    color.g += orange.g / 7.0;
    color.a += orange.a / 7.0;

    half4 yellow = content.eval(refractedCoord + dispersedCoord * (1.0 / 3.0));
    color.r += yellow.r / 3.5;
    color.g += yellow.g / 3.5;
    color.a += yellow.a / 7.0;

    half4 green = center;
    color.g += green.g / 3.5;
    color.a += green.a / 7.0;

    half4 cyan = content.eval(refractedCoord - dispersedCoord * (1.0 / 3.0));
    color.g += cyan.g / 3.5;
    color.b += cyan.b / 3.0;
    color.a += cyan.a / 7.0;

    half4 blue = content.eval(refractedCoord - dispersedCoord * (2.0 / 3.0));
    color.b += blue.b / 3.0;
    color.a += blue.a / 7.0;

    half4 purple = content.eval(refractedCoord - dispersedCoord);
    color.r += purple.r / 7.0;
    color.b += purple.b / 3.0;
    color.a += purple.a / 7.0;

    if (diagnosticMode > 1.5 && diagnosticMode < 2.5) {
        return half4(0.5 + (float3(color.rgb) - float3(center.rgb)) * 4.0, 1.0);
    }

    float centerAlpha = float(center.a);
    float outputAlpha = float(color.a);
    float3 outputRgb = float3(color.rgb);
    if (strength <= 0.0 || centerAlpha <= epsilon || outputAlpha <= epsilon) return color;
    if (
        outputAlpha > 1.0 + epsilon ||
        outputRgb.r < -epsilon || outputRgb.g < -epsilon || outputRgb.b < -epsilon ||
        outputRgb.r > outputAlpha + epsilon ||
        outputRgb.g > outputAlpha + epsilon ||
        outputRgb.b > outputAlpha + epsilon
    ) return color;

    float3 centerStraight = float3(center.rgb) / centerAlpha;
    float centerLuma = dot(centerStraight, float3(0.2126, 0.7152, 0.0722));
    float3 centerChroma = centerStraight - float3(centerLuma);
    float centerChromaLength = length(centerChroma);

    float accentAlpha = float(accentColor.a);
    if (accentAlpha <= epsilon) return color;
    float3 accentStraight = float3(accentColor.rgb) / accentAlpha;
    float accentLuma = dot(accentStraight, float3(0.2126, 0.7152, 0.0722));
    float3 accentChroma = accentStraight - float3(accentLuma);
    float accentChromaLength = length(accentChroma);
    if (centerChromaLength <= epsilon || accentChromaLength <= epsilon) return color;

    float hueSimilarity = dot(
        centerChroma / centerChromaLength,
        accentChroma / accentChromaLength
    );
    float saturation = centerChromaLength / max(abs(centerLuma), epsilon);
    float accentHueGate =
        smoothstep(0.80, 0.95, hueSimilarity) *
        smoothstep(0.05, 0.20, saturation);

    float edgeDistance = clamp(-sd, 0.0, refractionHeight);
    float outerGuard = smoothstep(0.10 * refractionHeight, 0.25 * refractionHeight, edgeDistance);
    float innerFade = 1.0 - smoothstep(0.70 * refractionHeight, refractionHeight, edgeDistance);
    float vertical = smoothstep(0.65, 0.90, abs(grad.y));
    float topBottomBand = outerGuard * innerFade * vertical;

    float3 dispersionDelta = outputRgb - float3(center.rgb);
    float rainbowRatio = length(dispersionDelta) / max(length(float3(center.rgb)), epsilon);
    float rainbowProtection = 1.0 - smoothstep(0.10, 0.25, rainbowRatio);
    float correctionStrength = strength * topBottomBand * accentHueGate * rainbowProtection;
    if (correctionStrength <= 0.0) return color;

    float3 correctionDelta = (float3(centerLuma) - centerStraight) * centerAlpha;
    float safeScale = safeCorrectionScale(outputRgb, outputAlpha, correctionDelta);
    float appliedStrength = min(correctionStrength, safeScale);
    if (appliedStrength <= 0.0) return color;

    return half4(outputRgb + correctionDelta * appliedStrength, color.a);
}
""".trimIndent()
