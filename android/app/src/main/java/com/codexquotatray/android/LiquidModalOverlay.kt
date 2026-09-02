package com.codexquotatray.android

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.dialog
import androidx.compose.ui.semantics.dismiss
import androidx.compose.ui.semantics.paneTitle
import androidx.compose.ui.semantics.semantics

@Composable
internal fun LiquidModalOverlay(
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
        val scrimModifier = Modifier
            .fillMaxSize()
            .background(Color.Black.copy(alpha = 0.32f))
        if (dismissOnClickOutside) {
            Box(
                scrimModifier
                    .clearAndSetSemantics { }
                    .clickable(
                        interactionSource = null,
                        indication = null,
                        onClick = { currentOnDismiss.value() },
                    ),
            )
        } else {
            ModalScrimPointerBlocker(scrimModifier)
        }
        Box(
            Modifier.fillMaxSize(),
            contentAlignment = Alignment.Center,
        ) {
            ModalSurfacePointerBarrier(
                modifier = Modifier.semantics {
                    this.paneTitle = paneTitle
                    dialog()
                    if (dismissOnBackPress || dismissOnClickOutside) {
                        dismiss("关闭") {
                            currentOnDismiss.value()
                            true
                        }
                    }
                },
                content = content,
            )
        }
    }
}

@Composable
private fun ModalSurfacePointerBarrier(
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit,
) {
    Box(
        modifier.pointerInput(Unit) {
            awaitPointerEventScope {
                while (true) {
                    val event = awaitPointerEvent(PointerEventPass.Final)
                    event.changes.forEach { change ->
                        if (!change.isConsumed) {
                            change.consume()
                        }
                    }
                }
            }
        },
    ) {
        content()
    }
}

@Composable
private fun ModalScrimPointerBlocker(modifier: Modifier) {
    Box(
        modifier.pointerInput(Unit) {
            awaitPointerEventScope {
                while (true) {
                    val event = awaitPointerEvent(PointerEventPass.Final)
                    event.changes.forEach { change ->
                        if (!change.isConsumed) {
                            change.consume()
                        }
                    }
                }
            }
        },
    )
}
