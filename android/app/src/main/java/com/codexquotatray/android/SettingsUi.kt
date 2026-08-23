package com.codexquotatray.android

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** Settings-specific visual tokens. They intentionally do not affect quota cards or the dock. */
internal object SettingsUiTokens {
    val sectionLabelInset = 8.dp
    val sectionToGroupGap = 10.dp
    val groupCornerRadius = 24.dp
    val groupVerticalPadding = 2.dp
    val rowMinHeight = 58.dp
    val rowHorizontalPadding = 20.dp
    val dividerInset = 20.dp
    val actionHeight = 52.dp
    val actionCornerRadius = 18.dp
    val actionHorizontalInset = 12.dp
    val actionInnerInset = 4.dp
    val actionEdgeInset = 10.dp
    val segmentedHeight = 48.dp
    val segmentedCornerRadius = 16.dp
    val segmentedBottomInset = 10.dp
}

internal fun settingsPalette(base: ThemePalette, effectiveTheme: ThemeMode): ThemePalette =
    if (effectiveTheme == ThemeMode.DARK) {
        base.copy(
            background = 0xff000000.toInt(),
            surface = 0xff252525.toInt(),
            border = 0xff343434.toInt(),
            title = 0xfff5f5f5.toInt(),
            body = 0xffeeeeee.toInt(),
            secondary = 0xff969696.toInt(),
            muted = 0xff8d8d8d.toInt(),
            secondaryButton = 0xff333333.toInt(),
            secondaryButtonText = 0xfff2f2f2.toInt(),
            progressTrack = 0xff3a3a3a.toInt(),
        )
    } else {
        base
    }

@Composable
internal fun SettingsSection(
    title: String,
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    Column(verticalArrangement = Arrangement.spacedBy(SettingsUiTokens.sectionToGroupGap)) {
        Text(
            text = title,
            color = palette.color(palette.secondary),
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
            modifier = Modifier.padding(start = SettingsUiTokens.sectionLabelInset),
        )
        content()
    }
}

@Composable
internal fun SettingsGroup(
    content: @Composable ColumnScope.() -> Unit,
) {
    val palette = LocalQuotaPalette.current
    Card(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(SettingsUiTokens.groupCornerRadius),
        colors = CardDefaults.cardColors(containerColor = palette.color(palette.surface)),
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(vertical = SettingsUiTokens.groupVerticalPadding),
            content = content,
        )
    }
}

@Composable
internal fun SettingsDivider() {
    val palette = LocalQuotaPalette.current
    HorizontalDivider(
        modifier = Modifier.padding(horizontal = SettingsUiTokens.dividerInset),
        thickness = 0.5.dp,
        color = palette.color(palette.border).copy(alpha = 0.8f),
    )
}

@Composable
internal fun SettingsNavigationRow(
    title: String,
    trailing: String? = null,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnClick = rememberSystemHapticClick(onClick)
    SettingsRow(
        enabled = enabled,
        onClick = hapticOnClick,
        role = Role.Button,
    ) {
        Text(
            text = title,
            modifier = Modifier.weight(1f),
            color = palette.color(palette.body),
            fontSize = 16.sp,
            fontWeight = FontWeight.Medium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        trailing?.let {
            Text(
                text = it,
                color = palette.color(palette.secondary),
                fontSize = 15.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.width(8.dp))
        }
        Icon(
            painter = painterResource(R.drawable.ic_chevron_right),
            contentDescription = null,
            modifier = Modifier.size(22.dp),
            tint = palette.color(palette.secondary),
        )
    }
}

@Composable
internal fun SettingsToggleRow(
    title: String,
    checked: Boolean,
    description: String? = null,
    enabled: Boolean = true,
    onChange: (Boolean) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnChange = rememberSystemHapticChange(onChange)
    SettingsRow(
        enabled = enabled,
        onClick = { hapticOnChange(!checked) },
        role = Role.Switch,
    ) {
        if (description.isNullOrBlank()) {
            Text(
                text = title,
                modifier = Modifier.weight(1f),
                color = palette.color(palette.body),
                fontSize = 16.sp,
                fontWeight = FontWeight.Medium,
            )
        } else {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = title,
                    color = palette.color(palette.body),
                    fontSize = 16.sp,
                    fontWeight = FontWeight.Medium,
                )
                Text(
                    text = description,
                    color = palette.color(palette.secondary),
                    fontSize = 13.sp,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        Switch(
            checked = checked,
            onCheckedChange = if (enabled) hapticOnChange else null,
            enabled = enabled,
            colors = SwitchDefaults.colors(
                checkedThumbColor = Color.White,
                checkedTrackColor = palette.color(palette.accent),
                uncheckedThumbColor = Color(0xFFF1F1F1),
                uncheckedTrackColor = Color(0xFF4A4A4A),
                uncheckedBorderColor = Color.Transparent,
            ),
        )
    }
}

@Composable
internal fun SettingsSelectionRow(
    title: String,
    selected: Boolean,
    onClick: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnClick = rememberSystemHapticClick(onClick)
    SettingsRow(
        onClick = hapticOnClick,
        role = Role.RadioButton,
    ) {
        Text(
            text = title,
            modifier = Modifier.weight(1f),
            color = palette.color(palette.body),
            fontSize = 16.sp,
            fontWeight = FontWeight.Medium,
        )
        if (selected) {
            Icon(
                painter = painterResource(R.drawable.ic_check),
                contentDescription = null,
                modifier = Modifier.size(22.dp),
                tint = palette.color(palette.accent),
            )
        } else {
            Spacer(Modifier.size(22.dp))
        }
    }
}

@Composable
internal fun SettingsInfoRow(
    title: String,
    value: String,
    valueColor: Color? = null,
    valueMaxLines: Int = 2,
) {
    val palette = LocalQuotaPalette.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = SettingsUiTokens.rowMinHeight)
            .padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = title,
            modifier = Modifier.weight(1f),
            color = palette.color(palette.body),
            fontSize = 16.sp,
            fontWeight = FontWeight.Medium,
        )
        Spacer(Modifier.width(16.dp))
        Text(
            text = value,
            color = valueColor ?: palette.color(palette.secondary),
            fontSize = 14.sp,
            maxLines = valueMaxLines,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
internal fun SettingsInlineLabel(
    label: String,
    enabled: Boolean = true,
) {
    val palette = LocalQuotaPalette.current
    Text(
        text = label,
        color = palette.color(palette.secondary),
        fontSize = 13.sp,
        fontWeight = FontWeight.Medium,
        modifier = Modifier
            .alpha(if (enabled) 1f else 0.45f)
            .padding(
                start = SettingsUiTokens.rowHorizontalPadding,
                end = SettingsUiTokens.rowHorizontalPadding,
                top = 10.dp,
                bottom = 2.dp,
            ),
    )
}

@Composable
internal fun SettingsWarningCaption(text: String) {
    Text(
        text = text,
        color = CodexColors.danger,
        fontSize = 13.sp,
        fontWeight = FontWeight.Medium,
        modifier = Modifier.padding(
            start = SettingsUiTokens.rowHorizontalPadding,
            end = SettingsUiTokens.rowHorizontalPadding,
            top = 10.dp,
            bottom = 2.dp,
        ),
    )
}

@Composable
internal fun SettingsActionButton(
    label: String,
    danger: Boolean = false,
    enabled: Boolean = true,
    primary: Boolean = false,
    horizontalInset: Dp = SettingsUiTokens.actionHorizontalInset,
    topPadding: Dp = SettingsUiTokens.actionInnerInset,
    bottomPadding: Dp = SettingsUiTokens.actionInnerInset,
    onClick: () -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnClick = rememberSystemHapticClick(onClick)
    val container = if (primary) {
        palette.color(palette.primaryButton)
    } else {
        palette.color(palette.secondaryButton)
    }
    val content = when {
        danger -> CodexColors.danger
        primary -> palette.color(palette.onPrimary)
        else -> palette.color(palette.secondaryButtonText)
    }
    Button(
        onClick = hapticOnClick,
        modifier = Modifier
            .fillMaxWidth()
            .padding(
                start = horizontalInset,
                top = topPadding,
                end = horizontalInset,
                bottom = bottomPadding,
            )
            .height(SettingsUiTokens.actionHeight),
        enabled = enabled,
        shape = RoundedCornerShape(SettingsUiTokens.actionCornerRadius),
        contentPadding = PaddingValues(horizontal = 16.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = container,
            contentColor = content,
            disabledContainerColor = container.copy(alpha = 0.45f),
            disabledContentColor = content.copy(alpha = 0.55f),
        ),
    ) {
        Text(text = label, fontSize = 15.sp, fontWeight = FontWeight.Medium)
    }
}

@Composable
internal fun SettingsSegmentedSelector(
    options: List<SettingsSegmentOption>,
    selectedValue: Int,
    enabled: Boolean,
    onSelected: (Int) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(
                start = SettingsUiTokens.actionHorizontalInset,
                end = SettingsUiTokens.actionHorizontalInset,
                top = SettingsUiTokens.segmentedBottomInset,
                bottom = SettingsUiTokens.segmentedBottomInset,
            )
            .height(SettingsUiTokens.segmentedHeight)
            .clip(RoundedCornerShape(SettingsUiTokens.segmentedCornerRadius))
            .background(palette.color(palette.secondaryButton))
            .alpha(if (enabled) 1f else 0.45f)
            .padding(3.dp),
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        options.forEach { option ->
            SettingsSegment(
                option = option,
                selected = option.value == selectedValue,
                enabled = enabled,
                onSelected = onSelected,
            )
        }
    }
}

internal data class SettingsSegmentOption(
    val value: Int,
    val label: String,
)

@Composable
private fun RowScope.SettingsSegment(
    option: SettingsSegmentOption,
    selected: Boolean,
    enabled: Boolean,
    onSelected: (Int) -> Unit,
) {
    val palette = LocalQuotaPalette.current
    val hapticOnClick = rememberSystemHapticClick { onSelected(option.value) }
    Box(
        modifier = Modifier
            .weight(1f)
            .fillMaxHeight()
            .clip(RoundedCornerShape(13.dp))
            .background(if (selected) palette.color(palette.accent) else Color.Transparent)
            .clickable(enabled = enabled, role = Role.RadioButton, onClick = hapticOnClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = option.label,
            color = if (selected) Color.White else palette.color(palette.secondaryButtonText),
            fontSize = 14.sp,
            fontWeight = FontWeight.Medium,
        )
    }
}

@Composable
internal fun SettingsTextInput(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    visualTransformation: VisualTransformation = VisualTransformation.None,
) {
    val palette = LocalQuotaPalette.current
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = SettingsUiTokens.actionHorizontalInset, vertical = 4.dp),
        label = { Text(label) },
        singleLine = true,
        visualTransformation = visualTransformation,
        shape = RoundedCornerShape(16.dp),
        colors = OutlinedTextFieldDefaults.colors(
            focusedBorderColor = palette.color(palette.accent),
            focusedLabelColor = palette.color(palette.accent),
            cursorColor = palette.color(palette.accent),
            unfocusedBorderColor = palette.color(palette.border),
            unfocusedLabelColor = palette.color(palette.secondary),
        ),
    )
}

@Composable
private fun SettingsRow(
    enabled: Boolean = true,
    onClick: () -> Unit,
    role: Role,
    content: @Composable RowScope.() -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = SettingsUiTokens.rowMinHeight)
            .clickable(enabled = enabled, role = role, onClick = onClick)
            .alpha(if (enabled) 1f else 0.45f)
            .padding(horizontal = SettingsUiTokens.rowHorizontalPadding),
        verticalAlignment = Alignment.CenterVertically,
        content = content,
    )
}
