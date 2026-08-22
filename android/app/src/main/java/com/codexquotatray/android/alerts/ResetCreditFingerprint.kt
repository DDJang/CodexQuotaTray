package com.codexquotatray.android.alerts

import com.codexquotatray.android.protocol.ResetCredit
import java.security.MessageDigest

object ResetCreditFingerprint {
    fun create(credit: ResetCredit): String = create(
        resetType = credit.resetType,
        grantedAt = credit.grantedAt,
        expiresAt = credit.expiresAt,
        title = credit.title,
    )

    fun create(
        resetType: String?,
        grantedAt: Long?,
        expiresAt: Long?,
        title: String?,
    ): String {
        val canonical = listOf(
            normalize(resetType),
            grantedAt?.toString().orEmpty(),
            expiresAt?.toString().orEmpty(),
            normalize(title),
        ).joinToString("\u001f")
        return MessageDigest.getInstance("SHA-256")
            .digest(canonical.toByteArray(Charsets.UTF_8))
            .joinToString("") { byte -> "%02x".format(byte) }
    }

    private fun normalize(value: String?): String = value?.trim().orEmpty()
}
