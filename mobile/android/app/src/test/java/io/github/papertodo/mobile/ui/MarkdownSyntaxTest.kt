package io.github.papertodo.mobile.ui

import org.junit.Assert.*
import org.junit.Test

class MarkdownSyntaxTest {
    @Test fun desktopImageIdsAndWidthAttributesAreKeptWithoutRewritingSource() {
        val source = "![image|320x240,80%](i:001){width=75%}\r\n\r\n![second](i:12345678)\n"
        val plan = MarkdownSyntax.compile(source)
        assertEquals(source, plan.source)
        assertEquals(listOf("001", "12345678"), plan.images.map { it.id })
        assertNull(plan.images[0].width)
        assertEquals(75.0, plan.images[0].percent!!, 0.0)
        assertEquals(source.indexOf("![second]"), plan.images[1].start)
    }

    @Test fun imagesInsideFencedCodeOrInlineTextAreNeverLoaded() {
        val source = "```md\n![image](i:001)\n```\n~~~\n![image](i:002)\n~~~\n" +
            "text ![image](i:003)\n![web](https://example.com/a.png)\n![invalid](i:12)\n![valid](i:004)"
        assertEquals(listOf("004"), MarkdownSyntax.compile(source).images.map { it.id })
    }

    @Test fun inlineAndBlockSpansReferToOriginalCrLfOffsets() {
        val source = "# 标题\r\n\r\n**加粗**、*斜体*、~~删除~~ 和 `code`\r\n> 引用\r\n- 清单\r\n[链接](https://example.com)"
        val plan = MarkdownSyntax.compile(source)
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.HEADING && it.value == 1 })
        assertEquals("**加粗**", plan.decorations.first { it.kind == MarkdownKind.BOLD }.let { source.substring(it.start, it.end) })
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.ITALIC })
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.STRIKE })
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.QUOTE })
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.BULLET })
        val link = plan.decorations.first { it.kind == MarkdownKind.LINK }
        assertEquals("链接", source.substring(link.start, link.end))
        assertEquals("https://example.com", link.target)
        assertTrue(plan.decorations.all { it.start >= 0 && it.end <= source.length && it.start < it.end })
    }

    @Test fun supportedInlineHtmlIsStyledButSourceIsUnchanged() {
        val source = "<b>粗体</b> <u>下划线</u> <a href='https://example.com'>链接</a>"
        val plan = MarkdownSyntax.compile(source)
        assertEquals(source, plan.source)
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.BOLD && source.substring(it.start, it.end) == "粗体" })
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.UNDERLINE })
        assertTrue(plan.decorations.any { it.kind == MarkdownKind.LINK && it.target == "https://example.com" })
    }
}
