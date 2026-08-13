package com.codexquotatray.android

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.LinkInteractionListener
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.codexquotatray.android.update.ReleaseNotesBlock
import com.codexquotatray.android.update.ReleaseNotesBlockKind
import com.codexquotatray.android.update.ReleaseNotesInline
import com.codexquotatray.android.update.ReleaseNotesInlineKind
import com.codexquotatray.android.update.ReleaseNotesMarkdown

@Composable
internal fun ReleaseNotesMarkdownView(
    markdown: String,
    modifier: Modifier = Modifier,
) {
    val document = remember(markdown) { ReleaseNotesMarkdown.parse(markdown) }
    if (document.blocks.isEmpty() && !document.wasTruncated) return

    Column(
        modifier = modifier.fillMaxWidth(),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        document.blocks.forEach { block ->
            ReleaseNotesBlockView(block)
        }
        if (document.wasTruncated) {
            Text(
                text = "（说明已截断）",
                color = LocalContentColor.current.copy(alpha = 0.72f),
                fontSize = 12.sp,
            )
        }
    }
}

@Composable
private fun ReleaseNotesBlockView(block: ReleaseNotesBlock) {
    when (block.kind) {
        ReleaseNotesBlockKind.HEADING -> ReleaseNotesInlineText(
            inlines = block.inlines,
            fontSize = when (block.level) {
                1 -> 19.sp
                2 -> 17.sp
                else -> 15.sp
            },
            fontWeight = FontWeight.SemiBold,
        )

        ReleaseNotesBlockKind.PARAGRAPH -> ReleaseNotesInlineText(block.inlines)

        ReleaseNotesBlockKind.UNORDERED_LIST_ITEM -> ReleaseNotesListItem(
            prefix = "•",
            inlines = block.inlines,
        )

        ReleaseNotesBlockKind.ORDERED_LIST_ITEM -> ReleaseNotesListItem(
            prefix = "${block.listIndex}.",
            inlines = block.inlines,
        )

        ReleaseNotesBlockKind.QUOTE -> Row(Modifier.fillMaxWidth()) {
            Box(
                modifier = Modifier
                    .width(3.dp)
                    .heightIn(min = 20.dp)
                    .background(MaterialTheme.colorScheme.outline),
            )
            ReleaseNotesInlineText(
                inlines = block.inlines,
                modifier = Modifier.padding(start = 8.dp).weight(1f),
            )
        }

        ReleaseNotesBlockKind.CODE_BLOCK -> Text(
            text = block.inlines.firstOrNull()?.text.orEmpty(),
            modifier = Modifier
                .fillMaxWidth()
                .background(
                    color = MaterialTheme.colorScheme.surfaceVariant,
                    shape = RoundedCornerShape(5.dp),
                )
                .padding(horizontal = 10.dp, vertical = 8.dp),
            color = LocalContentColor.current,
            fontFamily = FontFamily.Monospace,
            fontSize = 12.sp,
            lineHeight = 16.sp,
        )
    }
}

@Composable
private fun ReleaseNotesListItem(
    prefix: String,
    inlines: List<ReleaseNotesInline>,
) {
    Row(Modifier.fillMaxWidth()) {
        Text(
            text = prefix,
            modifier = Modifier.width(22.dp),
            color = LocalContentColor.current,
            fontSize = 13.sp,
            lineHeight = 19.sp,
        )
        ReleaseNotesInlineText(
            inlines = inlines,
            modifier = Modifier.weight(1f),
        )
    }
}

@Composable
private fun ReleaseNotesInlineText(
    inlines: List<ReleaseNotesInline>,
    modifier: Modifier = Modifier,
    fontSize: TextUnit = 13.sp,
    fontWeight: FontWeight = FontWeight.Normal,
) {
    val uriHandler = LocalUriHandler.current
    val textColor = LocalContentColor.current
    val linkColor = MaterialTheme.colorScheme.primary
    val annotated = remember(inlines, fontSize, fontWeight, textColor, linkColor, uriHandler) {
        buildReleaseNotesAnnotatedString(
            inlines = inlines,
            textColor = textColor,
            linkColor = linkColor,
            onLinkClick = { url -> runCatching { uriHandler.openUri(url) } },
        )
    }

    Text(
        text = annotated,
        modifier = modifier,
        style = TextStyle(
            color = textColor,
            fontSize = fontSize,
            lineHeight = if (fontSize.value >= 15f) fontSize * 1.25f else 19.sp,
            fontWeight = fontWeight,
        ),
    )
}

private fun buildReleaseNotesAnnotatedString(
    inlines: List<ReleaseNotesInline>,
    textColor: androidx.compose.ui.graphics.Color,
    linkColor: androidx.compose.ui.graphics.Color,
    onLinkClick: (String) -> Unit,
): AnnotatedString = buildAnnotatedString {
    inlines.forEach { inline ->
        when (inline.kind) {
            ReleaseNotesInlineKind.TEXT -> append(inline.text)
            ReleaseNotesInlineKind.BOLD -> withStyle(SpanStyle(fontWeight = FontWeight.Bold)) {
                append(inline.text)
            }
            ReleaseNotesInlineKind.ITALIC -> withStyle(SpanStyle(fontStyle = FontStyle.Italic)) {
                append(inline.text)
            }
            ReleaseNotesInlineKind.INLINE_CODE -> withStyle(
                SpanStyle(
                    fontFamily = FontFamily.Monospace,
                    background = textColor.copy(alpha = 0.12f),
                ),
            ) {
                append(inline.text)
            }
            ReleaseNotesInlineKind.LINK -> {
                val url = inline.url
                if (url != null) {
                    pushLink(
                        LinkAnnotation.Url(
                            url = url,
                            styles = TextLinkStyles(
                                style = SpanStyle(
                                    color = linkColor,
                                    textDecoration = TextDecoration.Underline,
                                ),
                            ),
                            linkInteractionListener = object : LinkInteractionListener {
                                override fun onClick(link: LinkAnnotation) {
                                    onLinkClick(url)
                                }
                            },
                        ),
                    )
                    append(inline.text)
                    pop()
                } else {
                    append(inline.text)
                }
            }
        }
    }
}
