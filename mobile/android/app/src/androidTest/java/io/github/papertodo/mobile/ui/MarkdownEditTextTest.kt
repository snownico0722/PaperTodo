package io.github.papertodo.mobile.ui

import android.os.SystemClock
import android.graphics.Bitmap
import android.graphics.Color
import android.text.style.ReplacementSpan
import android.text.style.StyleSpan
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import java.io.File

class MarkdownEditTextTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()

    @Test fun renderingStylesMarkdownWithoutChangingItsCharactersOrSelection() {
        val source = "# 纸片\n\n**加粗** 和 *斜体*\n- 待办\n"
        lateinit var editor: MarkdownEditText
        instrumentation.runOnMainSync {
            editor = MarkdownEditText(instrumentation.targetContext)
            editor.setText(source)
            editor.setSelection(9, 11)
            editor.refreshRendering()
        }
        await { editor.text.getSpans(0, editor.length(), StyleSpan::class.java).isNotEmpty() }
        instrumentation.runOnMainSync {
            assertEquals(source, editor.text.toString())
            assertEquals(9, editor.selectionStart)
            assertEquals(11, editor.selectionEnd)
            editor.renderMode = "off"
            editor.refreshRendering()
        }
        await { editor.text.getSpans(0, editor.length(), StyleSpan::class.java).isEmpty() }
    }

    @Test fun refreshingDuringChineseCompositionKeepsImeSpansAndOriginalSource() {
        val source = "**正在输入中文**"
        lateinit var editor: MarkdownEditText
        lateinit var connection: InputConnection
        instrumentation.runOnMainSync {
            editor = MarkdownEditText(instrumentation.targetContext)
            editor.setText(source)
            editor.setSelection(2, 8)
            connection = editor.onCreateInputConnection(EditorInfo())!!
            assertTrue(connection.setComposingRegion(2, 8))
            editor.refreshRendering()
        }
        SystemClock.sleep(260)
        instrumentation.runOnMainSync {
            assertEquals(source, editor.text.toString())
            assertEquals(2, BaseInputConnection.getComposingSpanStart(editor.text))
            assertEquals(8, BaseInputConnection.getComposingSpanEnd(editor.text))
            connection.finishComposingText()
        }
        await { editor.text.getSpans(0, editor.length(), StyleSpan::class.java).isNotEmpty() }
        instrumentation.runOnMainSync { assertEquals(source, editor.text.toString()) }
    }

    @Test fun allThreeDesktopRenderModesKeepTheSameMarkdownAndSelection() {
        val source = "# 标题\n**粗体** ~~删除~~ [链接](https://example.com)\n> 引用\n- 列表"
        lateinit var editor: MarkdownEditText
        instrumentation.runOnMainSync {
            editor = MarkdownEditText(instrumentation.targetContext)
            editor.setText(source)
            editor.setSelection(7, 9)
        }
        for (mode in listOf("basic", "enhanced", "off")) {
            instrumentation.runOnMainSync { editor.renderMode = mode; editor.refreshRendering() }
            if (mode != "off") await { editor.text.getSpans(0, editor.length(), StyleSpan::class.java).isNotEmpty() }
            else await { editor.text.getSpans(0, editor.length(), StyleSpan::class.java).isEmpty() }
            instrumentation.runOnMainSync {
                assertEquals(source, editor.text.toString())
                assertEquals(7, editor.selectionStart)
                assertEquals(9, editor.selectionEnd)
            }
        }
    }

    @Test fun localImagePreviewNeverReplacesTheSavedImageReference() {
        val context = instrumentation.targetContext
        val file = File.createTempFile("markdown-render-", ".png", context.cacheDir)
        val bitmap = Bitmap.createBitmap(8, 6, Bitmap.Config.ARGB_8888).apply { eraseColor(Color.GREEN) }
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        bitmap.recycle()
        val source = "本地图片\n![image|100%](i:001){width=80%}\n"
        lateinit var editor: MarkdownEditText
        try {
            instrumentation.runOnMainSync {
                editor = MarkdownEditText(context)
                editor.isFocusable = false
                editor.imageResolver = { id -> if (id == "001") file else null }
                editor.setText(source)
                editor.setSelection(2)
                editor.refreshRendering()
            }
            await { editor.text.getSpans(0, editor.length(), ReplacementSpan::class.java).isNotEmpty() }
            instrumentation.runOnMainSync {
                assertEquals(source, editor.text.toString())
                assertEquals(2, editor.selectionStart)
                editor.imageReferenceMode = "hidden"
                editor.refreshRendering()
                assertEquals(source, editor.text.toString())
                editor.renderMode = "off"
                editor.refreshRendering()
            }
            await { editor.text.getSpans(0, editor.length(), ReplacementSpan::class.java).isEmpty() }
            instrumentation.runOnMainSync {
                assertEquals(source, editor.text.toString())
                editor.imageResolver = null
            }
        } finally { file.delete() }
    }

    @Test fun desktopPaletteValuesAndUnknownSchemeFallbackAreExact() {
        val context = instrumentation.targetContext
        val warm = PaperTheme.colors(context, "light", "warm")
        assertEquals(Color.rgb(255, 249, 234), warm.paper)
        assertEquals(Color.rgb(224, 206, 167), warm.border)
        assertEquals(Color.rgb(51, 41, 30), warm.text)
        assertEquals(Color.rgb(176, 98, 66), warm.link)
        val forestDark = PaperTheme.colors(context, "dark", "forest")
        assertEquals(Color.rgb(26, 30, 27), forestDark.paper)
        assertEquals(Color.rgb(128, 190, 150), forestDark.link)
        assertEquals(warm, PaperTheme.colors(context, "light", "unknown"))
        assertFalse(warm.isDark)
        assertTrue(forestDark.isDark)
    }

    private fun await(condition: () -> Boolean) {
        val deadline = SystemClock.uptimeMillis() + 4000
        while (SystemClock.uptimeMillis() < deadline) {
            var ready = false
            instrumentation.runOnMainSync { ready = condition() }
            if (ready) return
            SystemClock.sleep(25)
        }
        fail("Markdown rendering did not finish within 4 seconds")
    }
}
