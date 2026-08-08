package com.codexquotatray.android

import android.content.Context
import android.graphics.Canvas
import android.util.AttributeSet
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView

/**
 * A source view for QmDeve Liquid Glass. Registered glass controls are hidden
 * only while the library records this source, preventing recursive self-capture
 * without taking a bitmap snapshot or changing the normal UI hierarchy.
 */
internal interface LiquidGlassExclusionHost {
    fun registerLiquidGlassView(view: View)
    fun unregisterLiquidGlassView(view: View)
}

internal class LiquidGlassBackdropLayout @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : LinearLayout(context, attrs), LiquidGlassExclusionHost {
    private val excludedViews = LinkedHashSet<View>()

    override fun registerLiquidGlassView(view: View) {
        if (view !== this) excludedViews.add(view)
    }

    override fun unregisterLiquidGlassView(view: View) {
        excludedViews.remove(view)
    }

    override fun dispatchDraw(canvas: Canvas) {
        val hidden = excludedViews.mapNotNull { view ->
            if (view.visibility == VISIBLE && isDescendant(view)) {
                view to view.visibility
            } else {
                null
            }
        }
        hidden.forEach { (view, _) -> view.visibility = INVISIBLE }
        try {
            super.dispatchDraw(canvas)
        } finally {
            hidden.forEach { (view, visibility) -> view.visibility = visibility }
        }
    }

    private fun isDescendant(view: View): Boolean {
        var current: android.view.ViewParent? = view.parent
        while (current != null) {
            if (current === this) return true
            current = current.parent
        }
        return false
    }
}

internal class LiquidGlassBackdropScrollView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : ScrollView(context, attrs), LiquidGlassExclusionHost {
    private val excludedViews = LinkedHashSet<View>()

    override fun registerLiquidGlassView(view: View) {
        if (view !== this) excludedViews.add(view)
    }

    override fun unregisterLiquidGlassView(view: View) {
        excludedViews.remove(view)
    }

    override fun dispatchDraw(canvas: Canvas) {
        val hidden = excludedViews.mapNotNull { view ->
            if (view.visibility == VISIBLE && isDescendant(view)) {
                view to view.visibility
            } else {
                null
            }
        }
        hidden.forEach { (view, _) -> view.visibility = INVISIBLE }
        try {
            super.dispatchDraw(canvas)
        } finally {
            hidden.forEach { (view, visibility) -> view.visibility = visibility }
        }
    }

    private fun isDescendant(view: View): Boolean {
        var current: android.view.ViewParent? = view.parent
        while (current != null) {
            if (current === this) return true
            current = current.parent
        }
        return false
    }
}
