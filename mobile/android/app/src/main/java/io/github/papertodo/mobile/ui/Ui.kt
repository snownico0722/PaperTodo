package io.github.papertodo.mobile.ui

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.ContextWrapper
import android.graphics.Typeface
import android.os.Build
import android.view.View
import android.view.WindowManager
import android.widget.TextView
import io.github.papertodo.mobile.data.PaperRepository
import java.io.File

fun Context.dp(value: Float): Int = (value * resources.displayMetrics.density + .5f).toInt()
fun Context.dp(value: Int): Int = dp(value.toFloat())

fun Context.activity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> if (baseContext !== this) baseContext.activity() else null
    else -> null
}

fun AlertDialog.showFor(context: Context): AlertDialog {
    if (context.activity() == null) window?.setType(WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY)
    show()
    return this
}

fun PaperRepository.palette(context: Context): PaperColors = PaperTheme.colors(
    context, settingString("theme", "system"), settingString("colorScheme", "warm")
)

fun PaperRepository.font(context: Context): Typeface {
    val file = File(context.filesDir, "custom-font.ttf")
    return if (file.isFile) runCatching { Typeface.createFromFile(file) }.getOrDefault(Typeface.DEFAULT) else Typeface.DEFAULT
}

fun textButton(context: Context, label: String, description: String, color: Int, action: () -> Unit): TextView =
    TextView(context).apply {
        text = label
        contentDescription = description
        textSize = 19f
        gravity = android.view.Gravity.CENTER
        setTextColor(color)
        minWidth = context.dp(42)
        minHeight = context.dp(48)
        isFocusable = true
        setOnClickListener { action() }
        val out = android.util.TypedValue()
        context.theme.resolveAttribute(android.R.attr.selectableItemBackgroundBorderless, out, true)
        if (out.resourceId != 0) setBackgroundResource(out.resourceId)
    }

fun View.hideKeyboard() {
    val manager = context.getSystemService(Context.INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager
    manager.hideSoftInputFromWindow(windowToken, 0)
    clearFocus()
}
