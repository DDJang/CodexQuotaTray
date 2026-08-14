package com.codexquotatray.android

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Handler
import com.codexquotatray.android.quota.QuotaSnapshotStore
import com.codexquotatray.android.quota.QuotaRefreshScheduler
import com.codexquotatray.android.usage.TokenSyncEndpoint
import com.codexquotatray.android.usage.TokenSyncPairing
import com.codexquotatray.android.usage.TokenSyncStore
import com.codexquotatray.android.usage.TokenUsagePairingLifecycle
import com.codexquotatray.android.usage.cacheIdentity
import com.codexquotatray.android.usage.TokenUsageRefreshScheduler
import com.codexquotatray.android.usage.TokenUsageSyncCoordinator
import com.codexquotatray.android.usage.TokenUsageSyncResult
import com.google.zxing.integration.android.IntentIntegrator
import java.util.concurrent.ExecutorService

/** The single QR pairing flow shared by Settings and the Token Usage page. */
internal object TokenPairingFlow {
    fun launchScan(activity: Activity) {
        IntentIntegrator(activity)
            .setDesiredBarcodeFormats("QR_CODE")
            .setPrompt("扫描 Windows Token Usage 配对二维码")
            .setBeepEnabled(false)
            .setOrientationLocked(false)
            .initiateScan()
    }

    fun parseScanResult(requestCode: Int, resultCode: Int, data: Intent?): TokenPairingScanResult? {
        val result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data) ?: return null
        val contents = result.contents?.takeIf { it.isNotBlank() }
        return if (contents == null) {
            TokenPairingScanResult.Cancelled
        } else {
            TokenPairingScanResult.Pairing(runCatching { TokenSyncEndpoint.parsePairingUri(contents) })
        }
    }

    /** Persists the pairing and keeps both independent background schedulers current. */
    fun savePairing(context: Context, pairing: TokenSyncPairing): Boolean {
        val appContext = context.applicationContext
        val saved = TokenUsagePairingLifecycle.withLock {
            TokenSyncStore(appContext).save(pairing).also { success ->
                if (success) {
                    QuotaSnapshotStore(appContext).invalidateWindowsForPairing(pairing.cacheIdentity())
                }
            }
        }
        if (!saved) return false
        com.codexquotatray.android.widget.QuotaWidgetBridge.syncFromCurrentMainSnapshot(appContext)
        TokenUsageRefreshScheduler.schedule(appContext)
        QuotaRefreshScheduler.schedule(appContext)
        return true
    }

    /** Runs the same connection test after either UI has saved a QR pairing. */
    fun testPairing(
        context: Context,
        pairing: TokenSyncPairing,
        worker: ExecutorService,
        main: Handler,
        callback: (Result<TokenUsageSyncResult>) -> Unit,
    ) {
        val appContext = context.applicationContext
        worker.execute {
            val result = runCatching { TokenUsageSyncCoordinator(appContext).sync(pairing, forceRefresh = true) }
            main.post { callback(result) }
        }
    }
}

internal sealed interface TokenPairingScanResult {
    data class Pairing(val result: Result<TokenSyncPairing>) : TokenPairingScanResult
    data object Cancelled : TokenPairingScanResult
}
