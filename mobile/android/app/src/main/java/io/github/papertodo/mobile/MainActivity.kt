package io.github.papertodo.mobile

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.inputmethod.InputMethodManager
import android.widget.*
import androidx.core.content.FileProvider
import io.github.papertodo.mobile.data.ImportPreview
import io.github.papertodo.mobile.data.PaperRepository
import io.github.papertodo.mobile.overlay.OverlayService
import io.github.papertodo.mobile.ui.*
import java.io.File
import java.util.concurrent.Executors

class MainActivity : Activity() {
    private val repository: PaperRepository get() = (application as PaperApp).repository
    private val preferences by lazy { getSharedPreferences("mobile-ui", MODE_PRIVATE) }
    private lateinit var root: LinearLayout
    private lateinit var host: FrameLayout
    private var surface: PaperView? = null
    private var selected = ""
    private var pendingImagePaper: String? = null
    private var pendingImport: ImportPreview? = null
    private var exportZip = true
    private var resumeOverlayAfterPermission = false
    private var busyDialog: AlertDialog? = null
    private val worker = Executors.newSingleThreadExecutor()
    private var settingsDialog: AlertDialog? = null
    private val listener: () -> Unit = { runOnUiThread {
        if (!isFinishing && repository.paper(selected) == null && repository.papers.isNotEmpty()) showPaper(repository.papers.first().id)
    } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        repository.addListener(listener)
        pendingImagePaper = savedInstanceState?.getString("imagePaper")
        exportZip = savedInstanceState?.getBoolean("exportZip", true) ?: true
        selected = savedInstanceState?.getString("selected") ?: intent.getStringExtra(OverlayService.EXTRA_PAPER)
            ?: preferences.getString("selected", "").orEmpty()
        render()
        handleIntent(intent)
        repository.startupWarning?.let { message ->
            AlertDialog.Builder(this).setTitle("数据读取提示").setMessage(message).setPositiveButton("知道了", null).show()
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIntent(intent)
    }

    private fun handleIntent(intent: Intent) {
        intent.getStringExtra(OverlayService.EXTRA_PAPER)?.takeIf { repository.paper(it) != null }?.let { showPaper(it) }
        if (intent.getBooleanExtra("settings", false)) { intent.removeExtra("settings"); showSettings() }
        intent.getStringExtra("insert_image")?.let { intent.removeExtra("insert_image"); pickImage(it) }
        intent.getStringExtra("share_note")?.let { intent.removeExtra("share_note"); shareNote(it) }
    }

    override fun onResume() {
        super.onResume()
        OverlayService.collapse(this)
        if (resumeOverlayAfterPermission) {
            resumeOverlayAfterPermission = false
            if (Settings.canDrawOverlays(this)) requestNotificationThenFloat()
            else Toast.makeText(this, "可继续在 App 内使用，开启悬浮需要允许显示在其他应用上层", Toast.LENGTH_LONG).show()
        }
    }

    private fun render() {
        val colors = repository.palette(this)
        root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(colors.paper)
        }
        root.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                val ime = insets.getInsets(WindowInsets.Type.ime())
                view.setPadding(bars.left + dp(10), bars.top, bars.right + dp(10), maxOf(bars.bottom, ime.bottom) + dp(8))
            } else {
                @Suppress("DEPRECATION") view.setPadding(insets.systemWindowInsetLeft + dp(10), insets.systemWindowInsetTop, insets.systemWindowInsetRight + dp(10), insets.systemWindowInsetBottom + dp(8))
            }
            insets
        }
        val top = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        top.addView(textButton(this, "☷", "选择纸片", colors.active) { choosePaper() }, LinearLayout.LayoutParams(dp(44), dp(50)))
        top.addView(TextView(this).apply { text = "PaperTodo"; textSize = 17f; setTextColor(colors.active); typeface = repository.font(this@MainActivity) }, LinearLayout.LayoutParams(0, -2, 1f))
        top.addView(textButton(this, "悬浮", "开启悬浮纸片", colors.active) { enableOverlay() }.apply { textSize = 14f }, LinearLayout.LayoutParams(dp(58), dp(50)))
        top.addView(textButton(this, "⚙", "设置与数据迁移", colors.active) { showSettings() }, LinearLayout.LayoutParams(dp(44), dp(50)))
        root.addView(top, LinearLayout.LayoutParams(-1, dp(54)))
        host = FrameLayout(this)
        root.addView(host, LinearLayout.LayoutParams(-1, 0, 1f))
        setContentView(root)
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = if (Color.luminance(colors.paper) > .5f) View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR else 0
        val id = selected.takeIf { repository.paper(it) != null } ?: repository.papers.firstOrNull()?.id
            ?: if (!repository.isReadOnly) repository.createPaper("todo") else null
        if (id != null) showPaper(id) else host.addView(TextView(this).apply {
            text = "原始数据暂时无法读取，已保留文件。\n请从设置导入有效备份。"; setTextColor(colors.text); gravity = Gravity.CENTER; textSize = 16f
        }, FrameLayout.LayoutParams(-1, -1))
    }

    private fun showPaper(id: String) {
        if (repository.paper(id) == null) return
        selected = id
        preferences.edit().putString("selected", id).apply()
        val paper = surface ?: PaperView(this, repository).also { view ->
            view.onChoosePaper = { choosePaper() }
            view.onNewPaper = { type -> safely { showPaper(repository.createPaper(type)) } }
            view.onCollapse = { safely { root.hideKeyboard(); repository.flush(); moveTaskToBack(true) } }
            view.onSettings = { showSettings() }
            view.onOpenPaper = { showPaper(it) }
            view.onInsertImage = { pickImage(it) }
            view.onShareNote = { shareNote(it) }
            surface = view
        }
        if (paper.parent == null) { host.removeAllViews(); host.addView(paper, FrameLayout.LayoutParams(-1, -1)) }
        paper.bind(id)
    }

    private fun choosePaper() {
        val papers = repository.papers
        val labels = papers.map { (if (it.type == "todo") "✓  " else "✎  ") + it.title.ifBlank { "未命名" } } + listOf("＋ 新建待办纸", "＋ 新建笔记纸")
        AlertDialog.Builder(this).setTitle("选择纸片").setItems(labels.toTypedArray()) { _, index -> safely {
            if (index < papers.size) showPaper(papers[index].id)
            else showPaper(repository.createPaper(if (index == papers.size) "todo" else "note"))
        } }.setNegativeButton("关闭", null).show()
    }

    private fun enableOverlay() {
        if (repository.paper(selected) == null) return
        root.hideKeyboard()
        if (!Settings.canDrawOverlays(this)) {
            AlertDialog.Builder(this).setTitle("允许悬浮纸片")
                .setMessage("允许 PaperTodo 显示在其他应用上层后，纸片和胶囊就能浮在其他应用上。realme 手机中也可能称为“悬浮窗”。")
                .setNegativeButton("稍后", null).setPositiveButton("打开系统设置") { _, _ ->
                    resumeOverlayAfterPermission = true
                    startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
                }.show()
        } else requestNotificationThenFloat()
    }

    private fun requestNotificationThenFloat() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED &&
            !preferences.getBoolean("notificationAsked", false)) {
            preferences.edit().putBoolean("notificationAsked", true).apply()
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATION)
        } else startOverlay()
    }

    private fun startOverlay() = safely {
        check(!repository.isReadOnly && repository.paper(selected) != null) { "请先恢复有效数据，再开启悬浮纸片。" }
        repository.flush()
        OverlayService.start(this, selected)
        moveTaskToBack(true)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQUEST_NOTIFICATION) startOverlay()
    }

    private fun showSettings() {
        root.hideKeyboard()
        val colors = repository.palette(this)
        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(18), dp(6), dp(18), dp(12)) }
        fun section(text: String) = column.addView(TextView(this).apply { this.text = text; textSize = 13f; setTextColor(colors.active); setPadding(0, dp(14), 0, dp(6)) })
        fun action(label: String, block: () -> Unit) = column.addView(Button(this).apply {
            text = label; isAllCaps = false; minHeight = dp(48); setOnClickListener { block() }
        }, LinearLayout.LayoutParams(-1, -2))
        fun choice(label: String, key: String, labels: Array<String>, values: Array<String>, default: String) {
            val index = values.indexOf(repository.settingString(key, default)).coerceAtLeast(0)
            action("$label：${labels[index]}") {
                AlertDialog.Builder(this).setTitle(label).setSingleChoiceItems(labels, index) { dialog, selectedIndex ->
                    repository.setSetting(key, values[selectedIndex]); dialog.dismiss(); settingsDialog?.dismiss(); surface = null; render(); showSettings()
                }.setNegativeButton("取消", null).show()
            }
        }
        fun toggle(label: String, key: String, default: Boolean = false) {
            column.addView(Switch(this).apply {
                text = label; textSize = 14f; minHeight = dp(52); isChecked = repository.settingBoolean(key, default)
                setOnCheckedChangeListener { _, checked -> repository.setSetting(key, checked) }
            }, LinearLayout.LayoutParams(-1, -2))
        }
        section("外观")
        choice("主题", "theme", arrayOf("跟随系统", "浅色", "深色"), arrayOf("system", "light", "dark"), "system")
        choice("配色", "colorScheme", arrayOf("暖纸", "墨", "林", "霞"), arrayOf("warm", "ink", "forest", "rose"), "warm")
        choice("待办大小", "todoVisualSize", arrayOf("小", "中", "大"), arrayOf("small", "medium", "large"), "medium")
        choice("笔记字号", "noteTextSize", arrayOf("小", "中", "大"), arrayOf("small", "medium", "large"), "medium")
        choice("Markdown 显示", "markdownRenderMode", arrayOf("纯文本", "基础样式", "增强浏览"), arrayOf("off", "basic", "enhanced"), "enhanced")
        choice("图片标记", "imageReferenceTextMode", arrayOf("始终显示", "编辑时显示", "始终隐藏"), arrayOf("always", "editing", "hidden"), "always")
        toggle("待办文字加粗", "todoTextBold")
        toggle("笔记文字加粗", "noteTextBold")
        action("导入字体（TTF / OTF）") { pickDocument(REQUEST_FONT, "*/*") }
        if (File(filesDir, "custom-font.ttf").exists()) action("恢复系统字体") {
            File(filesDir, "custom-font.ttf").delete(); repository.setSetting("customFontVersion", System.currentTimeMillis().toString()); settingsDialog?.dismiss(); surface = null; render()
        }
        section("待办行为")
        toggle("完成项自动移到底部", "autoMoveCompletedTodosToBottom")
        toggle("完成后自动清除", "autoClearCompletedTodos")
        section("悬浮纸片")
        action(if (OverlayService.isRunning) "返回悬浮纸片" else "开启悬浮纸片") { settingsDialog?.dismiss(); enableOverlay() }
        if (OverlayService.isRunning) action("停止所有悬浮窗口") { OverlayService.stop(this); settingsDialog?.dismiss(); showSettings() }
        action("realme 后台设置说明") {
            AlertDialog.Builder(this).setTitle("realme GT5 Pro")
                .setMessage("首次使用请允许悬浮窗和通知。若切到后台后胶囊消失，可在应用信息的电池或耗电管理中允许后台活动，并在最近任务中锁定 PaperTodo。不同 realme UI 版本的名称可能不同。\n\n系统强制停止或重启后，请打开 App 再开启悬浮。")
                .setNegativeButton("关闭", null).setPositiveButton("打开应用信息") { _, _ -> startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName"))) }.show()
        }
        section("数据迁移")
        action("导入电脑 JSON / 完整传输包 ZIP") { settingsDialog?.dismiss(); pickDocument(REQUEST_IMPORT, "*/*") }
        action("导出完整传输包（含图片）") { chooseExport(true) }
        action("导出电脑 data.json（不含图片）") { chooseExport(false) }
        action("备份与迁移说明") {
            AlertDialog.Builder(this).setTitle("手动迁移")
                .setMessage("纯文字可直接导入电脑的 data.json。带图片时，请先退出电脑端，用配套 PaperTodoTransfer 工具生成 ZIP。\n\n手机导出 ZIP 后，用电脑工具恢复到一个新文件夹，再按说明替换电脑数据。\n\n每次导入会备份手机旧数据。备份只保存在本机，卸载应用会一并移除，请定期导出完整 ZIP。")
                .setPositiveButton("知道了", null).show()
        }
        action("导出最近一次导入前备份") { exportBackup() }
        section("关于")
        action("PaperTodo Mobile ${BuildConfig.VERSION_NAME} · 原项目与许可") { showLicense() }
        val scroll = ScrollView(this).apply { addView(column) }
        settingsDialog = AlertDialog.Builder(this).setTitle("设置").setView(scroll).setPositiveButton("完成") { _, _ ->
            surface = null; render()
        }.create().also { it.show() }
    }

    private fun pickDocument(code: Int, mime: String) = safely {
        startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT).setType(mime).addCategory(Intent.CATEGORY_OPENABLE), code)
    }

    private fun pickImage(id: String) {
        pendingImagePaper = id
        pickDocument(REQUEST_IMAGE, "image/*")
    }

    private fun chooseExport(zip: Boolean) {
        exportZip = zip
        settingsDialog?.dismiss()
        safely {
            startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                .setType(if (zip) "application/zip" else "application/json")
                .putExtra(Intent.EXTRA_TITLE, if (zip) "PaperTodo-${java.time.LocalDate.now()}.zip" else "data.json"), REQUEST_EXPORT)
        }
    }

    @Deprecated("Uses the platform document picker on all supported Android versions")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (resultCode != RESULT_OK) return
        val uri = data?.data ?: return
        when (requestCode) {
            REQUEST_IMAGE -> {
                val id = pendingImagePaper ?: selected
                pendingImagePaper = null
                background("正在插入图片…", {
                    repository.insertImage(uri, id)
                }) {
                    surface = null; render(); showPaper(id)
                }
            }
            REQUEST_IMPORT -> background("正在检查导入文件…", {
                contentResolver.openInputStream(uri)!!.buffered().use { input ->
                    input.mark(4); val a = input.read(); val b = input.read(); input.reset()
                    repository.inspectImport(input, a == 'P'.code && b == 'K'.code)
                }
            }) { preview -> confirmImport(preview) }
            REQUEST_EXPORT -> background("正在导出…", {
                contentResolver.openOutputStream(uri, "wt")!!.use { output -> if (exportZip) repository.exportArchive(output) else repository.exportJson(output) }
            }) { Toast.makeText(this, "已导出", Toast.LENGTH_SHORT).show() }
            REQUEST_FONT -> background("正在导入字体…", {
                val temporary = File(cacheDir, "font-import.tmp")
                try {
                    contentResolver.openInputStream(uri)!!.use { input -> temporary.outputStream().use { output ->
                        val buffer = ByteArray(8192); var total = 0
                        while (true) { val count = input.read(buffer); if (count < 0) break; total += count; require(total <= 20 * 1024 * 1024) { "字体文件过大" }; output.write(buffer, 0, count) }
                    } }
                    android.graphics.Typeface.createFromFile(temporary)
                    temporary.copyTo(File(filesDir, "custom-font.ttf"), true)
                } finally { temporary.delete() }
            }) { repository.setSetting("customFontVersion", System.currentTimeMillis().toString()); surface = null; render(); Toast.makeText(this, "字体已更新", Toast.LENGTH_SHORT).show() }
            REQUEST_BACKUP -> background("正在导出导入前备份…", {
                contentResolver.openOutputStream(uri, "wt")!!.use { output -> check(repository.exportPreviousImport(output)) { "没有找到导入前备份" } }
            }) { Toast.makeText(this, "已导出备份", Toast.LENGTH_SHORT).show() }
        }
    }

    private fun confirmImport(preview: ImportPreview) {
        pendingImport?.close(); pendingImport = preview
        val missing = if (preview.missingImageIds.isEmpty()) "" else "\n缺少 ${preview.missingImageIds.size} 张图片，继续后保留原引用，图片暂时无法显示。"
        AlertDialog.Builder(this).setTitle("导入并替换手机数据？")
            .setMessage("${preview.paperCount} 张纸片 · ${preview.todoCount} 条待办 · ${preview.imageCount} 张图片\n\n现有手机数据将先完整备份，再替换为导入内容。$missing")
            .setNegativeButton("取消") { _, _ -> preview.close(); pendingImport = null }
            .setOnCancelListener { preview.close(); pendingImport = null }
            .setPositiveButton("备份并导入") { _, _ ->
                OverlayService.stop(this)
                pendingImport = null
                background("正在备份并导入…", { try { repository.applyImport(preview) } finally { preview.close() } }) {
                    selected = repository.papers.firstOrNull()?.id.orEmpty(); surface = null; render()
                    Toast.makeText(this, "已导入，原数据已备份", Toast.LENGTH_LONG).show()
                }
            }.show()
    }

    private fun shareNote(id: String) = safely {
        val paper = repository.paper(id) ?: return@safely
        val directory = File(cacheDir, "shared").apply { mkdirs() }
        val file = File.createTempFile("PaperTodo-", ".md", directory)
        file.writeText(paper.content)
        val uri = FileProvider.getUriForFile(this, "$packageName.files", file)
        val send = Intent(Intent.ACTION_SEND).setType("text/markdown").putExtra(Intent.EXTRA_STREAM, uri)
            .putExtra(Intent.EXTRA_TITLE, paper.title).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        send.clipData = ClipData.newRawUri("Markdown", uri)
        startActivity(Intent.createChooser(send, "分享或用其他应用打开"))
    }

    private fun exportBackup() {
        if (!repository.hasPreviousImportBackup()) { Toast.makeText(this, "尚无导入前备份", Toast.LENGTH_SHORT).show(); return }
        background("正在检查备份…", { repository.previousImportBackupWarning() }) { warning ->
            fun choose() = safely {
                settingsDialog?.dismiss()
                startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                    .setType("application/zip").putExtra(Intent.EXTRA_TITLE, if (warning == null) "PaperTodo-before-import.zip" else "PaperTodo-recovery-backup.zip"), REQUEST_BACKUP)
            }
            if (warning == null) choose() else AlertDialog.Builder(this).setTitle("导出原始恢复备份")
                .setMessage(warning).setNegativeButton("取消", null).setPositiveButton("保留原始文件") { _, _ -> choose() }.show()
        }
    }

    private fun showLicense() {
        val text = listOf("NOTICE.md", "LICENSE.md", "THIRD-PARTY-NOTICES.md", "licenses/Apache-2.0.txt", "licenses/CommonMark-BSD-2-Clause.txt")
            .joinToString("\n\n") { name -> assets.open(name).bufferedReader().use { it.readText() } }
        val body = TextView(this).apply { this.text = text; textSize = 12f; setPadding(dp(20), dp(12), dp(20), dp(12)); setTextIsSelectable(true) }
        AlertDialog.Builder(this).setTitle("原项目与许可").setView(ScrollView(this).apply { addView(body) }).setNegativeButton("关闭", null)
            .setPositiveButton("原项目") { _, _ -> startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/snownico0722/PaperTodo"))) }.show()
    }

    private fun <T> background(message: String, task: () -> T, done: (T) -> Unit) {
        busyDialog?.dismiss()
        busyDialog = AlertDialog.Builder(this).setMessage(message).setCancelable(false).create().also { it.show() }
        worker.execute {
            val result = runCatching(task)
            runOnUiThread {
                if (isFinishing || isDestroyed) { (result.getOrNull() as? AutoCloseable)?.close(); return@runOnUiThread }
                busyDialog?.dismiss(); busyDialog = null
                result.fold(onSuccess = { value -> safely { done(value) } }, onFailure = { error -> showError(error) })
            }
        }
    }

    private fun safely(block: () -> Unit) { runCatching(block).onFailure { showError(it) } }
    private fun showError(error: Throwable) {
        AlertDialog.Builder(this).setTitle("操作未完成").setMessage(error.message ?: "请重试，现有数据已保留。")
            .setPositiveButton("知道了", null).show()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putString("selected", selected); outState.putString("imagePaper", pendingImagePaper); outState.putBoolean("exportZip", exportZip)
        super.onSaveInstanceState(outState)
    }
    override fun onPause() { repository.requestFlush(); super.onPause() }
    override fun onDestroy() {
        repository.removeListener(listener)
        pendingImport?.close()
        busyDialog?.dismiss(); settingsDialog?.dismiss()
        worker.shutdown()
        super.onDestroy()
    }
    companion object {
        private const val REQUEST_IMPORT = 1
        private const val REQUEST_EXPORT = 2
        private const val REQUEST_IMAGE = 3
        private const val REQUEST_FONT = 4
        private const val REQUEST_BACKUP = 5
        private const val REQUEST_NOTIFICATION = 6
    }
}
