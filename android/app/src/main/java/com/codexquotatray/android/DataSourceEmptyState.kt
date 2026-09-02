package com.codexquotatray.android

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

@Composable
internal fun DataSourceEmptyStateCard(
    message: String,
    onLoginOpenAi: () -> Unit,
    onPairWindows: () -> Unit,
    loginEnabled: Boolean = true,
) {
    val palette = LocalQuotaPalette.current
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface)),
    ) {
        Column(
            modifier = Modifier.padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(message, color = palette.color(palette.secondary))
            SettingsActionButton(
                label = "登录 OpenAI",
                primary = true,
                enabled = loginEnabled,
                horizontalInset = 0.dp,
                topPadding = 0.dp,
                bottomPadding = 0.dp,
                onClick = onLoginOpenAi,
            )
            SettingsActionButton(
                label = "扫码配对",
                horizontalInset = 0.dp,
                topPadding = 0.dp,
                bottomPadding = 0.dp,
                onClick = onPairWindows,
            )
        }
    }
}
