package com.codexquotatray.android.update

import android.content.Context
import android.content.Intent
import android.net.Uri
import okhttp3.HttpUrl.Companion.toHttpUrl

object UpdateBrowser {
    fun open(context: Context, rawUrl: String) {
        UpdateDownloadUrlSecurity.validateGithubUrl(rawUrl)
        context.startActivity(
            Intent(Intent.ACTION_VIEW, Uri.parse(rawUrl)).addCategory(Intent.CATEGORY_BROWSABLE),
        )
    }
}

internal object UpdateDownloadUrlSecurity {
    fun validateGithubUrl(raw: String) {
        val url = runCatching { raw.toHttpUrl() }
            .getOrElse { throw UpdateDownloadException("更新下载地址无效") }
        val host = url.host.lowercase()
        val allowedHost = host == "github.com" ||
            host.endsWith(".github.com") ||
            host == "githubusercontent.com" ||
            host.endsWith(".githubusercontent.com") ||
            host == "githubassets.com" ||
            host.endsWith(".githubassets.com")
        if (!url.isHttps || !allowedHost) {
            throw UpdateDownloadException("更新下载地址不受信任")
        }
    }
}
