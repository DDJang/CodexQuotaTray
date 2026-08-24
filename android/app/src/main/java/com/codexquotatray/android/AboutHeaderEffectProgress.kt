package com.codexquotatray.android

internal fun aboutHeaderEffectProgress(
    scrollOffsetPx: Float,
    overscrollDisplacementPx: Float,
    activationDistancePx: Float,
): Float {
    if (!scrollOffsetPx.isFinite() ||
        !overscrollDisplacementPx.isFinite() ||
        !activationDistancePx.isFinite() ||
        activationDistancePx <= 0f
    ) {
        return 0f
    }
    val normalScrollProgress = (scrollOffsetPx / activationDistancePx).coerceIn(0f, 1f)
    val upwardOverscrollPx = (-overscrollDisplacementPx).coerceAtLeast(0f)
    val overscrollProgress = (upwardOverscrollPx / activationDistancePx).coerceIn(0f, 1f)
    return maxOf(normalScrollProgress, overscrollProgress).coerceIn(0f, 1f)
}
