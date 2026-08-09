package com.codexquotatray.android;

import androidx.compose.ui.graphics.Shape;
import com.kyant.shapes.Capsule;

/** Java bridge for the Kyant Shapes artifact's newer Kotlin metadata. */
final class KyantShapes {
    private KyantShapes() {}

    static Shape capsule() {
        return new Capsule();
    }
}
