package com.codexquotatray.android.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import com.codexquotatray.android.R
import com.codexquotatray.android.quotaProgressArgb
import kotlin.math.roundToInt

/** Draws the small deterministic ring surface used by the RemoteViews widget. */
internal object QuotaWidgetRingRenderer {
    private const val RING_SIZE_DP = 92f
    private const val DOUBLE_RING_SIZE_DP = 82f
    private const val OUTER_STROKE_DP = 8f
    private const val INNER_STROKE_DP = 7f
    private const val RING_GAP_DP = 9f

    fun render(
        context: Context,
        outer: QuotaWidgetWindow,
        inner: QuotaWidgetWindow?,
    ): Bitmap {
        val density = context.resources.displayMetrics.density
        val ringSizeDp = if (inner == null) RING_SIZE_DP else DOUBLE_RING_SIZE_DP
        val scale = ringSizeDp / RING_SIZE_DP
        val size = (ringSizeDp * density).roundToInt()
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        val center = size / 2f
        val outerStroke = OUTER_STROKE_DP * scale * density
        val innerStroke = INNER_STROKE_DP * scale * density
        val gap = RING_GAP_DP * scale * density
        val trackColor = context.getColor(R.color.widget_ring_track)

        if (inner == null) {
            drawRing(
                canvas = canvas,
                center = center,
                radius = center - outerStroke / 2f,
                strokeWidth = outerStroke,
                percent = outer.remainingPercent,
                trackColor = trackColor,
            )
        } else {
            val outerRadius = center - outerStroke / 2f
            val innerRadius = outerRadius - outerStroke / 2f - gap - innerStroke / 2f
            drawRing(canvas, center, outerRadius, outerStroke, outer.remainingPercent, trackColor)
            drawRing(canvas, center, innerRadius, innerStroke, inner.remainingPercent, trackColor)
        }
        return bitmap
    }

    private fun drawRing(
        canvas: Canvas,
        center: Float,
        radius: Float,
        strokeWidth: Float,
        percent: Int?,
        trackColor: Int,
    ) {
        val bounds = RectF(center - radius, center - radius, center + radius, center + radius)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            this.strokeWidth = strokeWidth
            strokeCap = Paint.Cap.ROUND
        }
        paint.color = trackColor
        canvas.drawCircle(center, center, radius, paint)

        val progress = percent?.coerceIn(0, 100) ?: return
        if (progress == 0) return
        paint.color = quotaProgressArgb(progress)
        if (progress == 100) {
            canvas.drawCircle(center, center, radius, paint)
        } else {
            canvas.drawArc(bounds, -90f, progress / 100f * 360f, false, paint)
        }
    }
}
