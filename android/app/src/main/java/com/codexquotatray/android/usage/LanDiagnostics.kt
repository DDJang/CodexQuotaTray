package com.codexquotatray.android.usage

import android.content.Context
import com.codexquotatray.android.AppLogStore

fun interface LanDiagnosticLogger {
    fun record(message: String)
}

internal object NoOpLanDiagnosticLogger : LanDiagnosticLogger {
    override fun record(message: String) = Unit
}

internal class AndroidLanDiagnosticLogger(context: Context) : LanDiagnosticLogger {
    private val appContext = context.applicationContext

    override fun record(message: String) {
        AppLogStore.record(appContext, message)
    }
}
