package com.codexquotatray.android.update

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import java.io.File

enum class InstallUpdateResult { STARTED, PERMISSION_REQUIRED }

object UpdateInstaller {
    fun canRequestPackageInstalls(context: Context): Boolean =
        installPermissionGranted(Build.VERSION.SDK_INT, context.packageManager.canRequestPackageInstalls())

    internal fun installPermissionGranted(sdkInt: Int, packageManagerAllowsInstall: Boolean): Boolean =
        sdkInt < Build.VERSION_CODES.O || packageManagerAllowsInstall

    fun requestInstallPermission(context: Activity) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        runCatching {
            context.startActivity(
                Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:${context.packageName}"),
                ),
            )
        }
    }

    fun install(context: Activity, apk: File): InstallUpdateResult {
        require(apk.isFile && apk.extension.equals("apk", ignoreCase = true)) { "更新安装包不存在" }
        if (!canRequestPackageInstalls(context)) {
            requestInstallPermission(context)
            return InstallUpdateResult.PERMISSION_REQUIRED
        }
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", apk)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(intent)
        return InstallUpdateResult.STARTED
    }
}
