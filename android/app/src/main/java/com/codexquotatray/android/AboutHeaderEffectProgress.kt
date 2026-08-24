package com.codexquotatray.android

internal fun aboutHeaderEffectProgress(
    scrollOffsetPx: Float,
    activationDistancePx: Float,
): Float {
    if (!scrollOffsetPx.isFinite() ||
        !activationDistancePx.isFinite() ||
        activationDistancePx <= 0f
    ) {
        return 0f
    }
    return (scrollOffsetPx / activationDistancePx).coerceIn(0f, 1f)
}
