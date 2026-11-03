package io.github.papertodo.mobile.ui

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.Typeface
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.Spannable
import android.text.TextPaint
import android.text.TextUtils
import android.text.TextWatcher
import android.text.style.BackgroundColorSpan
import android.text.style.ClickableSpan
import android.text.style.ForegroundColorSpan
import android.text.style.LineBackgroundSpan
import android.text.style.QuoteSpan
import android.text.style.RelativeSizeSpan
import android.text.style.ReplacementSpan
import android.text.style.StrikethroughSpan
import android.text.style.StyleSpan
import android.text.style.TypefaceSpan
import android.text.style.UnderlineSpan
import android.util.AttributeSet
import android.util.LruCache
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.Toast
import java.io.File
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.min

/**
 * One native text surface for both browsing and editing. Rendering only adds owned spans;
 * it never replaces text, selection, IME spans, or the outer view's TextWatchers.
 */
class MarkdownEditText @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : EditText(context, attrs) {
    var renderMode: String = "enhanced"
        set(value) {
            field = value.takeIf { it == "off" || it == "basic" || it == "enhanced" } ?: "enhanced"
            if (initialized) revision.incrementAndGet()
            latestResult = null
            refreshRendering()
        }

    var colors: PaperColors = PaperTheme.colors(context)
        set(value) {
            field = value
            setTextColor(value.text)
            setHintTextColor(value.weak)
            setLinkTextColor(value.link)
            highlightColor = PaperTheme.overlay(value.paper, value.active, 68)
            refreshRendering()
        }

    var imageReferenceMode: String = "always"
        set(value) {
            field = value.takeIf { it == "always" || it == "editing" || it == "hidden" } ?: "always"
            refreshRendering()
        }

    var imageResolver: ((String) -> File?)? = null
        set(value) {
            field = value
            if (initialized) revision.incrementAndGet()
            latestResult = null
            refreshRendering()
        }

    private val main = Handler(Looper.getMainLooper())
    private val revision = AtomicLong()
    private var initialized = false
    private var applyingSpans = false
    private var latestResult: RenderResult? = null
    private var selectionImage: String? = null
    private var pressedLink: MarkdownLinkSpan? = null
    private var linkDownX = 0f
    private var linkDownY = 0f
    private val renderTask = Runnable { submitRendering() }
    private val density = resources.displayMetrics.density
    private fun dp(value: Float): Float = value * density

    init {
        gravity = Gravity.TOP or Gravity.START
        setBackgroundColor(Color.TRANSPARENT)
        setTextColor(colors.text)
        setHintTextColor(colors.weak)
        setLinkTextColor(colors.link)
        highlightColor = PaperTheme.overlay(colors.paper, colors.active, 68)
        textSize = 16f
        setPadding(dp(14f).toInt(), dp(12f).toInt(), dp(14f).toInt(), dp(18f).toInt())
        setLineSpacing(dp(3f), 1.08f)
        setSingleLine(false)
        setHorizontallyScrolling(false)
        inputType = android.text.InputType.TYPE_CLASS_TEXT or
            android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE or
            android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
        imeOptions = imeOptions or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        // Native selection handles and the IME remain in charge of editing.
        setTextIsSelectable(true)
        setSelectAllOnFocus(false)
        initialized = true
        addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                if (!applyingSpans) revision.incrementAndGet()
            }
            override fun afterTextChanged(s: Editable?) {
                if (!applyingSpans) scheduleRendering()
            }
        })
    }

    fun refreshRendering() {
        if (!initialized) return
        val cached = latestResult
        val source = text?.toString().orEmpty()
        if (cached != null && cached.plan.source == source && cached.availableWidth == availableWidth()) {
            if (!hasComposition()) applyResult(cached)
        } else scheduleRendering(0)
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        scheduleRendering(0)
    }

    override fun onDetachedFromWindow() {
        main.removeCallbacks(renderTask)
        revision.incrementAndGet()
        super.onDetachedFromWindow()
    }

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        if (initialized && w != oldw) {
            revision.incrementAndGet()
            latestResult = null
            scheduleRendering(0)
        }
    }

    override fun onFocusChanged(focused: Boolean, direction: Int, previouslyFocusedRect: Rect?) {
        super.onFocusChanged(focused, direction, previouslyFocusedRect)
        if (initialized) refreshRendering()
    }

    override fun onSelectionChanged(selStart: Int, selEnd: Int) {
        super.onSelectionChanged(selStart, selEnd)
        if (!initialized || applyingSpans) return
        val image = latestResult?.plan?.images?.firstOrNull {
            intersectsSelection(it.start, it.end, selStart, selEnd)
        }?.id
        if (image != selectionImage) {
            selectionImage = image
            refreshRendering()
        }
    }

    /** A link activates in browsing state; once focused, taps position the text cursor. */
    override fun onTouchEvent(event: MotionEvent): Boolean {
        fun linkAtEvent(): MarkdownLinkSpan? {
            val textLayout = layout
            if (textLayout != null) {
                val x = event.x - totalPaddingLeft + scrollX
                val y = event.y - totalPaddingTop + scrollY
                if (y < 0 || y > textLayout.height) return null
                val line = textLayout.getLineForVertical(y.toInt())
                if (x < textLayout.getLineLeft(line) || x > textLayout.getLineRight(line)) return null
                val offset = textLayout.getOffsetForHorizontal(line, x)
                return text?.getSpans(offset, offset, MarkdownLinkSpan::class.java)?.firstOrNull()
            }
            return null
        }
        if (event.actionMasked == MotionEvent.ACTION_DOWN && !hasFocus()) {
            pressedLink = linkAtEvent()
            linkDownX = event.x
            linkDownY = event.y
            if (pressedLink != null) return true
        } else if (pressedLink != null) {
            when (event.actionMasked) {
                MotionEvent.ACTION_UP -> {
                    val link = pressedLink
                    pressedLink = null
                    if (link != null && linkAtEvent() === link) link.onClick(this)
                    return true
                }
                MotionEvent.ACTION_CANCEL -> pressedLink = null
                MotionEvent.ACTION_MOVE -> {
                    if (kotlin.math.abs(event.x - linkDownX) < dp(10f) &&
                        kotlin.math.abs(event.y - linkDownY) < dp(10f)) return true
                    pressedLink = null
                }
            }
        }
        return super.onTouchEvent(event)
    }

    private fun scheduleRendering(delay: Long = 100) {
        if (!initialized) return
        main.removeCallbacks(renderTask)
        main.postDelayed(renderTask, delay)
    }

    private fun hasComposition(): Boolean {
        val value = text ?: return false
        return BaseInputConnection.getComposingSpanStart(value) >= 0
    }

    private fun availableWidth(): Int = (width - paddingLeft - paddingRight).coerceAtLeast(dp(120f).toInt())

    private fun submitRendering() {
        if (hasComposition()) {
            scheduleRendering(160)
            return
        }
        val currentRevision = revision.get()
        val source = text?.toString().orEmpty()
        val available = availableWidth()
        val resolver = imageResolver
        val showImages = renderMode != "off"
        worker.execute {
            if (revision.get() != currentRevision) return@execute
            val result = runCatching {
                val plan = MarkdownSyntax.compile(source)
                val images = if (resolver != null && showImages) plan.images.mapNotNull { reference ->
                    if (revision.get() != currentRevision) return@mapNotNull null
                    val file = runCatching { resolver(reference.id) }.getOrNull() ?: return@mapNotNull null
                    val loaded = loadImage(file, available) ?: return@mapNotNull null
                    val scale = (reference.percent ?: 100.0).coerceIn(1.0, 1000.0) / 100.0
                    val desired = when {
                        reference.attribute && reference.percent != null -> (available * scale).toInt()
                        reference.attribute && reference.width != null -> (reference.width * density).toInt()
                        reference.width != null -> (reference.width * density * scale).toInt()
                        reference.percent != null -> (loaded.naturalWidth * density * scale).toInt()
                        else -> min((loaded.naturalWidth * density).toInt(), available)
                    }.coerceIn(dp(24f).toInt().coerceAtMost(available), available)
                    val naturalHeight = loaded.naturalHeight.toDouble() * desired / loaded.naturalWidth
                    // Keep extreme 1px-wide images from creating a multi-million-pixel text line.
                    val fit = min(1.0, dp(4096f).toDouble() / naturalHeight)
                    DecodedImage(reference, loaded.bitmap, (desired * fit).toInt().coerceAtLeast(1),
                        (naturalHeight * fit).toInt().coerceAtLeast(1))
                } else emptyList()
                RenderResult(plan, images, available)
            }.getOrElse {
                // A malformed note remains editable; rendering must never prevent saving it.
                RenderResult(MarkdownPlan(source, emptyList(), emptyList()), emptyList(), available)
            }
            main.post {
                if (revision.get() == currentRevision && text?.toString() == source) {
                    latestResult = result
                    if (!hasComposition()) applyResult(result) else scheduleRendering(160)
                }
            }
        }
    }

    private fun applyResult(result: RenderResult) {
        val editable = text ?: return
        if (applyingSpans || editable.toString() != result.plan.source || hasComposition()) return
        applyingSpans = true
        beginBatchEdit()
        try {
            editable.getSpans(0, editable.length, MarkdownOwnedSpan::class.java).forEach(editable::removeSpan)
            if (renderMode == "off") return
            val browse = !hasFocus()
            val enhanced = renderMode == "enhanced" && browse
            val palette = colors

            fun add(span: Any, start: Int, end: Int) {
                if (start >= 0 && end <= editable.length && start < end) {
                    editable.setSpan(span, start, end, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
                }
            }

            for (d in result.plan.decorations) {
                when (d.kind) {
                    MarkdownKind.HEADING -> {
                        add(MarkdownStyleSpan(Typeface.BOLD), d.start, d.end)
                        val scale = when (d.value) { 1 -> 1.28f; 2 -> 1.20f; 3 -> 1.12f; else -> 1.04f }
                        add(MarkdownSizeSpan(scale), d.start, d.end)
                    }
                    MarkdownKind.BOLD -> add(MarkdownStyleSpan(Typeface.BOLD), d.start, d.end)
                    MarkdownKind.ITALIC -> add(MarkdownStyleSpan(Typeface.ITALIC), d.start, d.end)
                    MarkdownKind.STRIKE -> add(MarkdownStrikeSpan(), d.start, d.end)
                    MarkdownKind.UNDERLINE -> add(MarkdownUnderlineSpan(), d.start, d.end)
                    MarkdownKind.CODE -> {
                        add(MarkdownTypefaceSpan(), d.start, d.end)
                        add(MarkdownBackgroundSpan(palette.code), d.start, d.end)
                    }
                    MarkdownKind.CODE_BLOCK -> {
                        add(MarkdownTypefaceSpan(), d.start, d.end)
                        add(MarkdownCodeBlockSpan(palette.code), d.start, d.end)
                    }
                    MarkdownKind.QUOTE -> {
                        add(MarkdownQuoteSpan(palette.quote), d.start, d.end)
                        add(MarkdownForegroundSpan(palette.weak), d.start, d.end)
                    }
                    MarkdownKind.SYNTAX -> add(MarkdownForegroundSpan(if (enhanced) palette.faintSyntax else palette.active), d.start, d.end)
                    MarkdownKind.LINK -> {
                        add(MarkdownLinkSpan(d.target, palette.link), d.start, d.end)
                    }
                    MarkdownKind.BULLET -> if (enhanced) add(MarkdownBulletSpan(), d.start, d.end)
                        else add(MarkdownForegroundSpan(palette.active), d.start, d.end)
                    MarkdownKind.RULE -> if (enhanced) {
                        add(MarkdownRuleSpan(result.availableWidth, palette.quote, dp(1f)), d.start, d.end)
                    } else add(MarkdownForegroundSpan(palette.active), d.start, d.end)
                }
            }

            for (image in result.images) {
                val ref = image.reference
                // Editing an image's source line must expose all characters and retain native IME behavior.
                if (hasFocus() && intersectsSelection(ref.start, ref.end, selectionStart, selectionEnd)) continue
                val showReference = imageReferenceMode == "always" || (imageReferenceMode == "editing" && !browse)
                add(
                    MarkdownImageBlockSpan(image.bitmap, image.width, image.height, result.availableWidth,
                        if (showReference) result.plan.source.substring(ref.start, ref.end).trim() else null,
                        palette.weak, dp(8f)),
                    ref.start, ref.end,
                )
            }
        } finally {
            endBatchEdit()
            applyingSpans = false
        }
        invalidate()
    }

    private fun intersectsSelection(start: Int, end: Int, selectionStart: Int, selectionEnd: Int): Boolean {
        val left = min(selectionStart, selectionEnd)
        val right = max(selectionStart, selectionEnd)
        return left in start..end || right in start..end || (left >= 0 && left < start && right > end)
    }

    private fun loadImage(file: File, targetWidth: Int): LoadedImage? {
        if (!file.isFile || file.length() > 8L * 1024 * 1024) return null
        val cacheKey = "${file.absolutePath}:${file.lastModified()}:$targetWidth"
        synchronized(imageCache) { imageCache.get(cacheKey) }?.let { return it }
        val dimensions = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, dimensions)
        if (dimensions.outWidth <= 0 || dimensions.outHeight <= 0 || dimensions.outWidth > 4096 || dimensions.outHeight > 4096) return null
        var sample = 1
        while (dimensions.outWidth / (sample * 2) >= targetWidth) sample *= 2
        val bitmap = BitmapFactory.decodeFile(file.absolutePath, BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.ARGB_8888
        }) ?: return null
        val loaded = LoadedImage(bitmap, dimensions.outWidth, dimensions.outHeight)
        synchronized(imageCache) { imageCache.put(cacheKey, loaded) }
        return loaded
    }

    private data class DecodedImage(val reference: MarkdownImageReference, val bitmap: Bitmap, val width: Int, val height: Int)
    private data class LoadedImage(val bitmap: Bitmap, val naturalWidth: Int, val naturalHeight: Int)
    private data class RenderResult(val plan: MarkdownPlan, val images: List<DecodedImage>, val availableWidth: Int)

    companion object {
        private val worker = Executors.newFixedThreadPool(2) { task ->
            Thread(task, "PaperTodo-Markdown").apply { isDaemon = true }
        }
        private val imageCache = object : LruCache<String, LoadedImage>(16 * 1024 * 1024) {
            override fun sizeOf(key: String, value: LoadedImage): Int = value.bitmap.allocationByteCount
        }
    }
}

private interface MarkdownOwnedSpan
private class MarkdownForegroundSpan(color: Int) : ForegroundColorSpan(color), MarkdownOwnedSpan
private class MarkdownBackgroundSpan(color: Int) : BackgroundColorSpan(color), MarkdownOwnedSpan
private class MarkdownStyleSpan(style: Int) : StyleSpan(style), MarkdownOwnedSpan
private class MarkdownSizeSpan(scale: Float) : RelativeSizeSpan(scale), MarkdownOwnedSpan
private class MarkdownTypefaceSpan : TypefaceSpan("monospace"), MarkdownOwnedSpan
private class MarkdownStrikeSpan : StrikethroughSpan(), MarkdownOwnedSpan
private class MarkdownUnderlineSpan : UnderlineSpan(), MarkdownOwnedSpan
private class MarkdownQuoteSpan(color: Int) : QuoteSpan(color), MarkdownOwnedSpan

private class MarkdownLinkSpan(private val url: String, private val color: Int) : ClickableSpan(), MarkdownOwnedSpan {
    override fun updateDrawState(ds: TextPaint) { ds.color = color; ds.isUnderlineText = true }
    override fun onClick(widget: View) {
        val uri = runCatching { Uri.parse(url) }.getOrNull() ?: return
        if (uri.scheme?.lowercase() !in setOf("http", "https", "mailto")) return
        runCatching {
            widget.context.startActivity(Intent(Intent.ACTION_VIEW, uri).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        }.onFailure { Toast.makeText(widget.context, "没有可打开此链接的应用", Toast.LENGTH_SHORT).show() }
    }
}

private class MarkdownCodeBlockSpan(private val color: Int) : LineBackgroundSpan, MarkdownOwnedSpan {
    override fun drawBackground(c: Canvas, p: Paint, left: Int, right: Int, top: Int, baseline: Int, bottom: Int,
        text: CharSequence, start: Int, end: Int, lineNumber: Int) {
        val oldColor = p.color
        p.color = color
        c.drawRect(left.toFloat(), top.toFloat(), right.toFloat(), bottom.toFloat(), p)
        p.color = oldColor
    }
}

private class MarkdownBulletSpan : ReplacementSpan(), MarkdownOwnedSpan {
    override fun getSize(paint: Paint, text: CharSequence, start: Int, end: Int, fm: Paint.FontMetricsInt?): Int =
        ceil(max(paint.measureText(text, start, end), paint.measureText("•"))).toInt()
    override fun draw(canvas: Canvas, text: CharSequence, start: Int, end: Int, x: Float, top: Int, y: Int, bottom: Int, paint: Paint) {
        canvas.drawText("•", x, y.toFloat(), paint)
    }
}

private class MarkdownRuleSpan(private val width: Int, private val color: Int, private val thickness: Float) : ReplacementSpan(), MarkdownOwnedSpan {
    override fun getSize(paint: Paint, text: CharSequence, start: Int, end: Int, fm: Paint.FontMetricsInt?): Int = width
    override fun draw(canvas: Canvas, text: CharSequence, start: Int, end: Int, x: Float, top: Int, y: Int, bottom: Int, paint: Paint) {
        val p = Paint(paint).apply { this.color = this@MarkdownRuleSpan.color; strokeWidth = thickness }
        val center = y + (paint.fontMetrics.ascent + paint.fontMetrics.descent) / 2f
        canvas.drawLine(x, center, x + width, center, p)
    }
}

/** Reserves a block without inserting an object character into the saved Markdown. */
private class MarkdownImageBlockSpan(
    private val bitmap: Bitmap,
    private val width: Int,
    private val height: Int,
    private val availableWidth: Int,
    private val sourceLabel: String?,
    private val labelColor: Int,
    private val gap: Float,
) : ReplacementSpan(), MarkdownOwnedSpan {
    private var labelHeight = 0
    private var blockHeight = height
    override fun getSize(paint: Paint, text: CharSequence, start: Int, end: Int, fm: Paint.FontMetricsInt?): Int {
        labelHeight = if (sourceLabel != null) ceil(paint.fontSpacing).toInt() else 0
        blockHeight = height + labelHeight + gap.toInt()
        fm?.let {
            it.ascent = -blockHeight
            it.top = it.ascent
            it.descent = gap.toInt() / 2
            it.bottom = it.descent
        }
        return availableWidth
    }
    override fun draw(canvas: Canvas, text: CharSequence, start: Int, end: Int, x: Float, top: Int, y: Int, bottom: Int, paint: Paint) {
        var imageTop = y - blockHeight.toFloat()
        if (sourceLabel != null) {
            val labelPaint = TextPaint(paint).apply { color = labelColor }
            val label = TextUtils.ellipsize(sourceLabel, labelPaint, availableWidth.toFloat(), TextUtils.TruncateAt.MIDDLE)
            canvas.drawText(label, 0, label.length, x, imageTop - labelPaint.fontMetrics.ascent, labelPaint)
            imageTop += labelHeight
        }
        val target = android.graphics.RectF(x, imageTop, x + width, imageTop + height)
        canvas.drawBitmap(bitmap, null, target, Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG))
    }
}
