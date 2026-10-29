package io.github.papertodo.mobile.ui

import android.content.Context
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.drawable.GradientDrawable

/** The v3.31 palette names and values are shared with desktop transfer files. */
data class PaperColors(
    val paper: Int,
    val border: Int,
    val text: Int,
    val weak: Int,
    val active: Int,
    val code: Int,
    val quote: Int,
    val link: Int,
    val checkbox: Int,
    val tint: Int,
    val danger: Int,
) {
    val isDark: Boolean get() = Color.red(paper) + Color.green(paper) + Color.blue(paper) < 384
    val titleBar: Int get() = PaperTheme.overlay(paper, tint, if (isDark) 18 else 12)
    val faintSyntax: Int get() = PaperTheme.overlay(paper, text, if (isDark) 78 else 72)
}

object PaperTheme {
    private fun palette(vararg rgb: Int): PaperColors = PaperColors(
        rgb[0] or 0xff000000.toInt(), rgb[1] or 0xff000000.toInt(),
        rgb[2] or 0xff000000.toInt(), rgb[3] or 0xff000000.toInt(),
        rgb[4] or 0xff000000.toInt(), rgb[5] or 0xff000000.toInt(),
        rgb[6] or 0xff000000.toInt(), rgb[7] or 0xff000000.toInt(),
        rgb[8] or 0xff000000.toInt(), rgb[9] or 0xff000000.toInt(),
        rgb[10] or 0xff000000.toInt(),
    )

    // Paper, border, text, weak, active, code, quote, link, checkbox, tint, danger.
    // Copied from PaperTodo v3.31 Theme.cs, rather than approximating screenshot colors.
    private val palettes = mapOf(
        "warm" to (
            palette(0xfff9ea, 0xe0cea7, 0x33291e, 0x8a7a63, 0x8c7350, 0xf7edd2,
                0xd4be92, 0xb06242, 0xb4a078, 0x785c30, 0xb05a46) to
                palette(0x211f1c, 0x4c453d, 0xe7e0d4, 0x92897b, 0xa88e6a, 0x2d2a26,
                    0x5e564b, 0xd69678, 0x6e6455, 0xe6dfd3, 0xe66e5a)
            ),
        "ink" to (
            palette(0xf6f7f9, 0xd0d6de, 0x262c36, 0x767e8a, 0x5a6c86, 0xeceff3,
                0xc6ced8, 0x42689c, 0xaab4c2, 0x465a78, 0xbc5450) to
                palette(0x1a1c20, 0x3c424c, 0xdee3ea, 0x8a929e, 0x849cbc, 0x26292f,
                    0x4e5662, 0x84aad6, 0x606a78, 0xb4c8e4, 0xe0746c)
            ),
        "forest" to (
            palette(0xf3f8f1, 0xc8dac6, 0x26322a, 0x6e8070, 0x588260, 0xe9f2e7,
                0xc0d6c0, 0x3c8260, 0xa8c0a8, 0x466e50, 0xbc604c) to
                palette(0x1a1e1b, 0x3a463c, 0xdce4dc, 0x869488, 0x7ca886, 0x252a26,
                    0x4a5a4c, 0x80be96, 0x5c6e5e, 0xb4d0ba, 0xde7c68)
            ),
        "rose" to (
            palette(0xfdf5f6, 0xe4cdd2, 0x36262a, 0x8c7278, 0x9e6876, 0xf8ecee,
                0xe0c6cc, 0xb2546e, 0xd8b8c0, 0x965060, 0xbc524e) to
                palette(0x211c1e, 0x4e4044, 0xe8dcdf, 0x988489, 0xbe8694, 0x2c2628,
                    0x5c4c50, 0xe094aa, 0x604e52, 0xe0b4be, 0xe67264)
            ),
    )

    fun colors(context: Context, theme: String = "system", scheme: String = "warm"): PaperColors {
        val dark = when (theme) {
            "dark" -> true
            "light" -> false
            else -> context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK ==
                Configuration.UI_MODE_NIGHT_YES
        }
        val pair = palettes[scheme] ?: palettes.getValue("warm")
        return if (dark) pair.second else pair.first
    }

    /** Like GradientDrawable itself, radius and stroke width are in physical pixels. */
    fun rounded(fill: Int, stroke: Int = Color.TRANSPARENT, radius: Float = 16f): GradientDrawable =
        GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            setColor(fill)
            cornerRadius = radius
            if (Color.alpha(stroke) > 0) setStroke(1, stroke)
        }

    fun overlay(background: Int, foreground: Int, alpha: Int): Int {
        val a = alpha.coerceIn(0, 255)
        fun channel(bg: Int, fg: Int) = (bg * (255 - a) + fg * a) / 255
        return Color.rgb(
            channel(Color.red(background), Color.red(foreground)),
            channel(Color.green(background), Color.green(foreground)),
            channel(Color.blue(background), Color.blue(foreground)),
        )
    }
}
