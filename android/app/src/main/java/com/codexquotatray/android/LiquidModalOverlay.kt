package com.codexquotatray.android

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.changedToDownIgnoreConsumed
import androidx.compose.ui.input.pointer.changedToUpIgnoreConsumed
import androidx.compose.ui.input.pointer.pointerInput
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
        Box(
            Modifier
                .fillMaxSize()
                .background(Color.Black.copy(alpha = 0.32f))
                .pointerInput(dismissOnClickOutside) {
                    awaitPointerEventScope {
                        var pointerIsDown = false
                        var tapCandidate = false
                        while (true) {
                            val event = awaitPointerEvent(PointerEventPass.Final)
                            val changes = event.changes
                            if (!pointerIsDown && changes.any { it.changedToDownIgnoreConsumed() }) {
                                pointerIsDown = true
                                tapCandidate = true
                            } else if (pointerIsDown && changes.any { it.changedToDownIgnoreConsumed() }) {
                                tapCandidate = false
                            }
                            if (pointerIsDown) {
                                if (changes.any { it.position != it.previousPosition } ||
                                    changes.count { it.pressed } > 1
                                ) {
                                    tapCandidate = false
                                }
                                val released = changes.any { it.changedToUpIgnoreConsumed() }
                                changes.forEach { change ->
                                    if (!change.isConsumed) {
                                        change.consume()
                                    }
                                }
                                if (released) {
                                    if (shouldDismissModalScrimTap(dismissOnClickOutside, tapCandidate)) {
                                        currentOnDismiss.value()
                                    }
                                    pointerIsDown = false
                                    tapCandidate = false
                                }
                            } else {
                                changes.forEach { change ->
                                    if (!change.isConsumed) {
                                        change.consume()
                                    }
                                }
                            }
                        }
                    }
                },
        )
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

internal fun shouldDismissModalScrimTap(
    dismissOnClickOutside: Boolean,
    tapCandidate: Boolean,
): Boolean = dismissOnClickOutside && tapCandidate
