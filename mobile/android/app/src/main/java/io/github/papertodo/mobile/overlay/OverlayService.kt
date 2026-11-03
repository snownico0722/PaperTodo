package io.github.papertodo.mobile.overlay

import android.app.*
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.IBinder
import android.provider.Settings
import android.view.*
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import io.github.papertodo.mobile.MainActivity
import io.github.papertodo.mobile.PaperApp
import io.github.papertodo.mobile.R
import io.github.papertodo.mobile.data.PaperRepository
import io.github.papertodo.mobile.ui.*
import kotlin.math.abs

/** Owns all overlay windows. A foreground Activity is the only entry point for starting it. */
class OverlayService : Service() {
    private lateinit var repository: PaperRepository
    private lateinit var manager: WindowManager
    private val preferences by lazy { getSharedPreferences("mobile-windows", MODE_PRIVATE) }
    private var paper: PaperView? = null
    private var paperParams: WindowManager.LayoutParams? = null
    private var normalBounds: Rect? = null
    private var keyboardHeight = 0
    private val legacyInsets by lazy { Rect(0, dp(24), 0, dp(24)) }
    private val queues = mutableListOf<View>()
    private var lastQueueSignature = ""
    private val listener: () -> Unit = { android.os.Handler(mainLooper).post { reconcile() } }

    override fun onCreate() {
        super.onCreate()
        repository = (application as PaperApp).repository
        manager = getSystemService(WINDOW_SERVICE) as WindowManager
        createChannel()
        startForeground(NOTIFICATION_ID, notification())
        isRunning = true
        repository.addListener(listener)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (!Settings.canDrawOverlays(this) || repository.isReadOnly) { stopSelf(); return START_NOT_STICKY }
        when (intent?.action) {
            ACTION_STOP -> { stopSelf(); return START_NOT_STICKY }
            ACTION_COLLAPSE -> collapse()
            ACTION_NEW -> showPaper(repository.createPaper("todo"))
            else -> {
                val requested = intent?.getStringExtra(EXTRA_PAPER)
                val id = requested?.takeIf { repository.paper(it) != null }
                    ?: preferences.getString("active", null)?.takeIf { repository.paper(it) != null }
                    ?: repository.papers.firstOrNull()?.id
                    ?: repository.createPaper("todo")
                showPaper(id)
            }
        }
        reconcile()
        return START_NOT_STICKY
    }

    private fun showPaper(id: String) {
        if (repository.paper(id) == null) return
        paper?.hideKeyboard()
        saveBounds()
        activePaperId = id
        preferences.edit().putString("active", id).apply()
        val safe = paperSafeBounds()
        val width = preferences.getInt("$id.width", (safe.width() * .92f).toInt().coerceAtMost(dp(430)))
        val height = preferences.getInt("$id.height", (safe.height() * .62f).toInt())
        normalBounds = Rect(preferences.getInt("$id.x", safe.centerX() - width / 2), preferences.getInt("$id.y", safe.top + dp(70)), 0, 0).apply {
            right = left + width; bottom = top + height
        }
        clampNormal()
        val params = paperParams ?: overlayParams(normalBounds!!.width(), normalBounds!!.height()).also { paperParams = it }
        params.flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
        val surface = paper ?: PaperView(this, repository).also { view ->
            view.onChoosePaper = { openApp() }
            view.onNewPaper = { type -> showPaper(repository.createPaper(type)) }
            view.onCollapse = { collapse() }
            view.onSettings = { openApp(settings = true) }
            view.onOpenPaper = { showPaper(it) }
            view.onInsertImage = { openApp(imagePaper = it) }
            view.onShareNote = { openApp(sharePaper = it) }
            view.onRequestEditing = { focusForEditing() }
            view.onDrag = { dx, dy ->
                normalBounds?.offset(dx.toInt(), dy.toInt()); clampNormal(); applyBounds(); saveBounds()
            }
            view.onResize = { dx, dy ->
                normalBounds?.let { bounds -> bounds.right += dx.toInt(); bounds.bottom += dy.toInt() }
                clampNormal(); applyBounds(); saveBounds()
            }
            view.setOnApplyWindowInsetsListener { _, insets ->
                if (Build.VERSION.SDK_INT < 30) {
                    @Suppress("DEPRECATION")
                    val bars = Rect(insets.stableInsetLeft, insets.stableInsetTop, insets.stableInsetRight, insets.stableInsetBottom)
                    @Suppress("DEPRECATION")
                    val ime = (insets.systemWindowInsetBottom - bars.bottom).coerceAtLeast(0)
                    if (bars != legacyInsets || ime != keyboardHeight) {
                        legacyInsets.set(bars); keyboardHeight = ime
                        view.post { clampNormal(); applyBounds() }
                    }
                }
                insets
            }
            // Legacy Android has no fitInsetsTypes API; use its visible display frame.
            view.viewTreeObserver.addOnGlobalLayoutListener {
                if (Build.VERSION.SDK_INT < 30) {
                    val visible = Rect()
                    view.getWindowVisibleDisplayFrame(visible)
                    val occlusion = (safeBounds().bottom - visible.bottom).coerceAtLeast(0)
                    val next = if (occlusion > dp(100)) occlusion else 0
                    if (next != keyboardHeight) { keyboardHeight = next; view.post { applyBounds() } }
                }
            }
            paper = view
        }
        surface.bind(id)
        applyBounds()
        if (surface.parent == null && !windowOperation { manager.addView(surface, params) }) return
        lastQueueSignature = ""
        reconcile()
    }

    private fun focusForEditing() {
        val view = paper ?: return
        val params = paperParams ?: return
        if ((params.flags and WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE) == 0) return
        params.flags = WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
        params.softInputMode = WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE
        if (view.parent != null) windowOperation { manager.updateViewLayout(view, params) }
    }

    private fun collapse() {
        paper?.hideKeyboard()
        repository.requestFlush()
        saveBounds()
        paper?.let { removeWindow(it) }
        paper = null
        paperParams = null
        normalBounds = null
        keyboardHeight = 0
        activePaperId = null
        lastQueueSignature = ""
        reconcile()
    }

    private fun reconcile() {
        if (!isRunning) return
        if (!Settings.canDrawOverlays(this)) { stopSelf(); return }
        if (activePaperId != null && repository.paper(activePaperId!!) == null) collapse()
        val signature = repository.papers.joinToString { "${it.id}:${it.title}:${it.type}" } + ":" +
            repository.settingString("theme", "system") + repository.settingString("colorScheme", "warm") + activePaperId
        if (signature == lastQueueSignature) return
        lastQueueSignature = signature
        queues.forEach { removeWindow(it) }; queues.clear()
        val safe = safeBounds()
        val colors = repository.palette(this)
        for (side in listOf("left", "right")) {
            val selected = repository.papers.filter { preferences.getString("side.${it.id}", "right") == side }
            if (selected.isEmpty()) continue
            val order = preferences.getString("order.$side", "")!!.split(',')
            val sorted = selected.sortedBy { paper -> order.indexOf(paper.id).let { if (it < 0) Int.MAX_VALUE else it } }
            val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = if (side == "left") Gravity.START else Gravity.END }
            val collapsed = preferences.getBoolean("collapsed.$side", false)
            val master = capsule(if (collapsed) "☷  ${selected.size}" else "☷", side, false, master = true)
            column.addView(master)
            master.contentDescription = if (collapsed) "展开${sideLabel(side)}胶囊队列" else "收起${sideLabel(side)}胶囊队列"
            master.setOnClickListener { preferences.edit().putBoolean("collapsed.$side", !collapsed).apply(); lastQueueSignature = ""; reconcile() }
            master.setOnLongClickListener { openApp(settings = true); true }
            installQueueDrag(master, side, null)
            if (!collapsed) sorted.forEach { item ->
                val capsule = capsule((if (item.type == "todo") "✓  " else "✎  ") + item.title.ifBlank { "未命名" }, side, item.id == activePaperId)
                capsule.contentDescription = "打开纸片：${item.title}"
                column.addView(capsule, LinearLayout.LayoutParams(-2, dp(40)).apply { topMargin = dp(4) })
                capsule.setOnClickListener { if (activePaperId == item.id) collapse() else showPaper(item.id) }
                capsule.setOnLongClickListener { openApp(paperId = item.id); true }
                installQueueDrag(capsule, side, item.id)
            }
            val scroll = ScrollView(this).apply { isVerticalScrollBarEnabled = false; addView(column); clipToPadding = false }
            val y = preferences.getInt("top.$side", safe.top + dp(90)).coerceIn(safe.top, (safe.bottom - dp(48)).coerceAtLeast(safe.top))
            val expectedHeight = dp(if (collapsed) 40 else 40 + sorted.size * 44).coerceAtMost(safe.bottom - y)
            val params = overlayParams(-2, expectedHeight)
            params.gravity = Gravity.TOP or if (side == "left") Gravity.LEFT else Gravity.RIGHT
            params.x = if (side == "left") safe.left else (screenWidth() - safe.right).coerceAtLeast(0)
            params.y = y
            if (!windowOperation { manager.addView(scroll, params) }) return
            queues.add(scroll)
        }
        if (paper != null) { clampNormal(); applyBounds() }
    }

    private fun capsule(label: String, side: String, active: Boolean, master: Boolean = false): TextView {
        val colors = repository.palette(this)
        val radius = dp(13).toFloat()
        val background = GradientDrawable().apply {
            setColor(colors.paper)
            setStroke(dp(if (active) 2 else 1), if (active) colors.active else colors.border)
            cornerRadii = if (side == "left") floatArrayOf(0f,0f,radius,radius,radius,radius,0f,0f) else floatArrayOf(radius,radius,0f,0f,0f,0f,radius,radius)
        }
        return TextView(this).apply {
            text = if (paper != null) label.substringBefore("  ") else label
            textSize = if (master) 17f else 13f
            setTextColor(if (active) colors.active else colors.text)
            typeface = repository.font(this@OverlayService)
            setPadding(dp(12), 0, dp(12), 0)
            gravity = Gravity.CENTER_VERTICAL
            minHeight = dp(40)
            minWidth = dp(40)
            maxWidth = dp(if (paper != null) 40 else 175)
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
            this.background = background
            elevation = dp(3).toFloat()
        }
    }

    private fun installQueueDrag(view: View, side: String, id: String?) {
        var downX = 0f; var downY = 0f; var moved = false
        view.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downX = event.rawX; downY = event.rawY; moved = false; false }
                MotionEvent.ACTION_MOVE -> {
                    if (abs(event.rawX - downX) + abs(event.rawY - downY) > dp(12)) {
                        moved = true
                        view.parent?.requestDisallowInterceptTouchEvent(true)
                        view.alpha = .65f
                    }
                    moved
                }
                MotionEvent.ACTION_UP -> {
                    view.alpha = 1f
                    if (moved) {
                        val safe = safeBounds()
                        if (id == null) preferences.edit().putInt("top.$side", event.rawY.toInt().coerceIn(safe.top, safe.bottom - dp(48))).apply()
                        else {
                            val targetSide = if (event.rawX < safe.centerX()) "left" else "right"
                            val top = preferences.getInt("top.$targetSide", safe.top + dp(90))
                            val existing = preferences.getString("order.$targetSide", "")!!.split(',').filter { it.isNotBlank() && it != id }.toMutableList()
                            repository.papers.filter { it.id != id && preferences.getString("side.${it.id}", "right") == targetSide }.forEach { if (!existing.contains(it.id)) existing.add(it.id) }
                            existing.add(((event.rawY - top - dp(44)) / dp(44)).toInt().coerceIn(0, existing.size), id)
                            preferences.edit().putString("side.$id", targetSide).putString("order.$targetSide", existing.joinToString(",")).apply()
                        }
                        lastQueueSignature = ""; reconcile(); true
                    } else false
                }
                MotionEvent.ACTION_CANCEL -> { view.alpha = 1f; moved }
                else -> false
            }
        }
    }

    private fun clampNormal() {
        val safe = paperSafeBounds()
        val bounds = normalBounds ?: return
        val width = bounds.width().coerceIn(dp(280).coerceAtMost(safe.width()), safe.width())
        val height = bounds.height().coerceIn(dp(260).coerceAtMost(safe.height()), safe.height())
        val x = bounds.left.coerceIn(safe.left, safe.right - width)
        val y = bounds.top.coerceIn(safe.top, safe.bottom - height)
        bounds.set(x, y, x + width, y + height)
    }

    private fun applyBounds() {
        val bounds = normalBounds ?: return
        val params = paperParams ?: return
        val safe = paperSafeBounds()
        val bottom = (safe.bottom - keyboardHeight).coerceAtLeast(safe.top + dp(180))
        params.width = bounds.width()
        params.height = bounds.height().coerceAtMost(bottom - safe.top)
        params.x = bounds.left
        params.y = bounds.top.coerceAtMost(bottom - params.height).coerceAtLeast(safe.top)
        paper?.takeIf { it.parent != null }?.let { windowOperation { manager.updateViewLayout(it, params) } }
    }

    private fun saveBounds() {
        val id = activePaperId ?: return
        val bounds = normalBounds ?: return
        preferences.edit().putInt("$id.x", bounds.left).putInt("$id.y", bounds.top).putInt("$id.width", bounds.width()).putInt("$id.height", bounds.height()).apply()
    }

    private fun safeBounds(): Rect {
        if (Build.VERSION.SDK_INT >= 30) {
            val metrics = manager.maximumWindowMetrics
            val insets = metrics.windowInsets.getInsetsIgnoringVisibility(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
            return Rect(metrics.bounds.left + insets.left, metrics.bounds.top + insets.top, metrics.bounds.right - insets.right, metrics.bounds.bottom - insets.bottom)
        }
        val metrics = android.util.DisplayMetrics()
        @Suppress("DEPRECATION") manager.defaultDisplay.getRealMetrics(metrics)
        return Rect(legacyInsets.left, legacyInsets.top, metrics.widthPixels - legacyInsets.right, metrics.heightPixels - legacyInsets.bottom)
    }

    /** Expanded papers leave a narrow, reachable capsule rail on each occupied edge. */
    private fun paperSafeBounds(): Rect {
        val bounds = safeBounds()
        val sides = repository.papers.map { preferences.getString("side.${it.id}", "right") }.toSet()
        if (sides.contains("left")) bounds.left += dp(44)
        if (sides.contains("right")) bounds.right -= dp(44)
        return bounds
    }

    private fun screenWidth(): Int = if (Build.VERSION.SDK_INT >= 30) manager.maximumWindowMetrics.bounds.width() else resources.displayMetrics.widthPixels
    private fun sideLabel(side: String) = if (side == "left") "左侧" else "右侧"
    private fun overlayParams(width: Int, height: Int) = WindowManager.LayoutParams(width, height,
        WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
        WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
        PixelFormat.TRANSLUCENT).apply {
        gravity = Gravity.TOP or Gravity.LEFT
        softInputMode = WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE
        // The system fits this non-Activity window inside the visible IME frame. Keep
        // system-bar handling in safeBounds, so their offsets are not applied twice.
        if (Build.VERSION.SDK_INT >= 30) setFitInsetsTypes(WindowInsets.Type.ime())
        if (Build.VERSION.SDK_INT >= 28) layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_NEVER
    }

    private fun openApp(paperId: String? = activePaperId, settings: Boolean = false, imagePaper: String? = null, sharePaper: String? = null) {
        collapse()
        startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(EXTRA_PAPER, paperId).putExtra("settings", settings).putExtra("insert_image", imagePaper).putExtra("share_note", sharePaper))
    }

    private fun createChannel() {
        (getSystemService(NOTIFICATION_SERVICE) as NotificationManager).createNotificationChannel(
            NotificationChannel(CHANNEL, "悬浮纸片", NotificationManager.IMPORTANCE_LOW).apply { description = "开启悬浮后提供收起和停止入口" }
        )
    }

    private fun notification(): Notification {
        fun pending(action: String, code: Int) = PendingIntent.getService(this, code, Intent(this, OverlayService::class.java).setAction(action), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val content = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_notification).setContentTitle("PaperTodo 悬浮纸片已开启")
            .setContentText("点击打开纸片；可随时收起或停止悬浮")
            .setContentIntent(content).setOngoing(true).setCategory(Notification.CATEGORY_SERVICE)
            .addAction(Notification.Action.Builder(null, "新建", pending(ACTION_NEW, 1)).build())
            .addAction(Notification.Action.Builder(null, "收起", pending(ACTION_COLLAPSE, 2)).build())
            .addAction(Notification.Action.Builder(null, "停止", pending(ACTION_STOP, 3)).build()).build()
    }

    private fun removeWindow(view: View) { if (view.parent != null) runCatching { manager.removeViewImmediate(view) } }
    private fun windowOperation(action: () -> Unit): Boolean = try {
        action(); true
    } catch (_: SecurityException) {
        stopSelf(); false
    } catch (_: WindowManager.BadTokenException) {
        stopSelf(); false
    } catch (_: IllegalArgumentException) {
        stopSelf(); false
    }
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        clampNormal(); applyBounds(); lastQueueSignature = ""; reconcile(); paper?.refresh()
    }
    override fun onDestroy() {
        repository.removeListener(listener)
        saveBounds()
        paper?.hideKeyboard()
        paper?.let { removeWindow(it) }
        queues.forEach { removeWindow(it) }
        queues.clear()
        repository.requestFlush()
        isRunning = false; activePaperId = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val EXTRA_PAPER = "paper_id"
        const val ACTION_SHOW = "io.github.papertodo.SHOW"
        const val ACTION_COLLAPSE = "io.github.papertodo.COLLAPSE"
        const val ACTION_STOP = "io.github.papertodo.STOP"
        private const val ACTION_NEW = "io.github.papertodo.NEW"
        private const val CHANNEL = "paper-overlay"
        private const val NOTIFICATION_ID = 17
        @Volatile var isRunning = false
            private set
        @Volatile var activePaperId: String? = null
            private set
        fun start(context: Context, paperId: String) {
            context.startForegroundService(Intent(context, OverlayService::class.java).setAction(ACTION_SHOW).putExtra(EXTRA_PAPER, paperId))
        }
        fun collapse(context: Context) { if (isRunning) context.startService(Intent(context, OverlayService::class.java).setAction(ACTION_COLLAPSE)) }
        fun stop(context: Context) { context.stopService(Intent(context, OverlayService::class.java)) }
    }
}
