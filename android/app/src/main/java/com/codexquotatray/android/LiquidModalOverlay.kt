package com.codexquotatray.android

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.paneTitle
import androidx.compose.ui.semantics.semantics
import com.kyant.backdrop.Backdrop

@Composable
internal fun LiquidModalOverlay(
    backdrop: Backdrop,
    paneTitle: String,
    onDismiss: () -> Unit,
    dismissOnBackPress: Boolean = true,
    dismissOnClickOutside: Boolean = true,
    content: @Composable () -> Unit,
) {
    val currentOnDismiss = rememberUpdatedState(onDismiss)

    BackHandler(enabled = true) {
        if (dismissOnBackPress) {
            currentOnDismiss.value()
        }
    }

    Box(Modifier.fillMaxSize()) {
        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.32f))
                .pointerInput(dismissOnClickOutside) {
                    detectTapGestures {
                        if (dismissOnClickOutside) {
                            currentOnDismiss.value()
                        }
                    }
                },
        )
        Box(
            Modifier
                .fillMaxSize()
                .semantics { this.paneTitle = paneTitle },
            contentAlignment = Alignment.Center,
        ) {
            content()
        }
    }
}
