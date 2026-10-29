package io.github.papertodo.mobile.ui

import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Paint
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.text.TextUtils
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.*
import androidx.recyclerview.widget.ItemTouchHelper
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import io.github.papertodo.mobile.data.PaperRepository
import io.github.papertodo.mobile.data.TodoItem

/** One paper surface, shared by the foreground Activity and the floating window. */
class PaperView(context: Context, private val repository: PaperRepository) : LinearLayout(context) {
    var onChoosePaper: () -> Unit = {}
    var onNewPaper: (String) -> Unit = {}
    var onCollapse: () -> Unit = {}
    var onSettings: () -> Unit = {}
    var onOpenPaper: (String) -> Unit = {}
    var onInsertImage: (String) -> Unit = {}
    var onShareNote: (String) -> Unit = {}
    var onRequestEditing: () -> Unit = {}
    var onDrag: ((Float, Float) -> Unit)? = null
    var onResize: ((Float, Float) -> Unit)? = null
    var paperId: String = ""
        private set

    private var colors = repository.palette(context)
    private var titleView: TextView? = null
    private var note: MarkdownEditText? = null
    private var todoAdapter: TodoAdapter? = null
    private var counter: TextView? = null
    private var undoButton: TextView? = null
    private var redoButton: TextView? = null
    private var preferences = ""
    private var refreshing = false
    private var pendingImeEditor: EditText? = null
    private var pendingImeFocus: android.view.ViewTreeObserver.OnWindowFocusChangeListener? = null
    private val listener: () -> Unit = { post { refresh() } }

    init {
        orientation = VERTICAL
        isFocusableInTouchMode = true
        defaultFocusHighlightEnabled = false
        clipToOutline = true
        elevation = context.dp(9).toFloat()
        setPadding(context.dp(1), context.dp(1), context.dp(1), context.dp(1))
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        repository.addListener(listener)
        refresh()
    }

    override fun onDetachedFromWindow() {
        clearPendingIme()
        repository.removeListener(listener)
        super.onDetachedFromWindow()
    }

    fun bind(id: String) {
        if (paperId == id && childCount > 0) { refresh(); return }
        hideKeyboard()
        paperId = id
        rebuild()
    }

    fun refresh() {
        val paper = repository.paper(paperId) ?: return
        val nextPreferences = listOf("theme", "colorScheme", "noteTextSize", "todoVisualSize", "markdownRenderMode",
            "noteTextBold", "todoTextBold", "imageReferenceTextMode", "zoom", "customFontVersion").joinToString { key ->
            key + ":" + repository.settingString(key, "")
        } + ":night=" + (resources.configuration.uiMode and android.content.res.Configuration.UI_MODE_NIGHT_MASK)
        if (nextPreferences != preferences) { preferences = nextPreferences; rebuild(); return }
        titleView?.text = paper.title.ifBlank { if (paper.type == "todo") "待办" else "笔记" }
        refreshing = true
        note?.let { editor ->
            if (editor.text.toString() != paper.content) {
                val selection = editor.selectionStart.coerceAtLeast(0).coerceAtMost(paper.content.length)
                editor.setText(paper.content)
                editor.setSelection(selection)
            }
        }
        refreshing = false
        todoAdapter?.submit(paper.items)
        counter?.text = if (repository.lastSaveError != null) "尚未保存 · 点击查看" else if (paper.type == "todo") "${paper.items.count { !it.done }} 项待办" else "${paper.content.length} 字"
        counter?.setTextColor(if (repository.lastSaveError != null) colors.danger else colors.weak)
        undoButton?.alpha = if (repository.canUndo(paperId)) 1f else .3f
        redoButton?.alpha = if (repository.canRedo(paperId)) 1f else .3f
    }

    fun focusNote() { note?.let { prepareEditor(it); it.requestFocus() } }

    private fun rebuild() {
        val paper = repository.paper(paperId) ?: return
        val savedSelection = note?.selectionStart
        val wasEditing = note?.hasFocus() == true
        removeAllViews()
        note = null
        todoAdapter = null
        colors = repository.palette(context)
        background = PaperTheme.rounded(colors.paper, colors.border, context.dp(18).toFloat())
        val bar = LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(context.dp(8), 0, context.dp(3), 0)
            setBackgroundColor((colors.tint and 0x00FFFFFF) or 0x13000000)
        }
        addView(bar, LayoutParams(-1, context.dp(48)))
        bar.addView(textButton(context, if (paper.type == "todo") "☑" else "✎", "选择纸片", colors.active, onChoosePaper), LayoutParams(context.dp(36), -1))
        titleView = TextView(context).apply {
            textSize = 15f
            setTextColor(colors.text)
            typeface = repository.font(context)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            gravity = Gravity.CENTER_VERTICAL
            contentDescription = "纸片标题，点击修改，拖动移动"
            setOnClickListener { rename() }
            installDrag(this) { dx, dy -> onDrag?.invoke(dx, dy) }
        }
        bar.addView(titleView, LayoutParams(0, -1, 1f))
        bar.addView(textButton(context, "+✓", "新建待办纸", colors.active) { onNewPaper("todo") }, LayoutParams(context.dp(40), -1))
        bar.addView(textButton(context, "+✎", "新建笔记纸", colors.active) { onNewPaper("note") }, LayoutParams(context.dp(40), -1))
        bar.addView(textButton(context, "−", "收起纸片", colors.active, onCollapse), LayoutParams(context.dp(40), -1))
        addView(View(context).apply { setBackgroundColor(colors.border); alpha = .45f }, LayoutParams(-1, context.dp(1)))
        if (paper.type == "todo") buildTodos() else buildNote(paper.content)
        buildFooter(paper.type)
        refresh()
        if (wasEditing && note != null) {
            note!!.requestFocus()
            note!!.setSelection((savedSelection ?: 0).coerceIn(0, note!!.length()))
        }
    }

    private fun buildTodos() {
        val list = RecyclerView(context).apply {
            contentDescription = "待办列表"
            layoutManager = LinearLayoutManager(context)
            setPadding(context.dp(6), context.dp(8), context.dp(6), 0)
            clipToPadding = false
            itemAnimator = null
        }
        val adapter = TodoAdapter()
        todoAdapter = adapter
        list.adapter = adapter
        val touch = ItemTouchHelper(object : ItemTouchHelper.SimpleCallback(ItemTouchHelper.UP or ItemTouchHelper.DOWN, ItemTouchHelper.LEFT) {
            override fun isLongPressDragEnabled() = false
            override fun onMove(rv: RecyclerView, from: RecyclerView.ViewHolder, to: RecyclerView.ViewHolder): Boolean {
                val fromIndex = from.bindingAdapterPosition
                val toIndex = to.bindingAdapterPosition
                if (fromIndex < 0 || toIndex < 0) return false
                adapter.move(fromIndex, toIndex)
                return true
            }
            override fun clearView(rv: RecyclerView, holder: RecyclerView.ViewHolder) {
                super.clearView(rv, holder)
                repository.reorderTodos(paperId, adapter.items.map { it.id })
                adapter.dragging = false
                refresh()
            }
            override fun onSwiped(holder: RecyclerView.ViewHolder, direction: Int) {
                adapter.items.getOrNull(holder.bindingAdapterPosition)?.let { repository.deleteTodo(paperId, it.id) }
            }
            override fun getSwipeDirs(rv: RecyclerView, holder: RecyclerView.ViewHolder): Int =
                if ((holder as TodoHolder).editor.hasFocus()) 0 else super.getSwipeDirs(rv, holder)
        })
        adapter.touch = touch
        touch.attachToRecyclerView(list)
        addView(list, LayoutParams(-1, 0, 1f))
        val newItem = EditText(context).apply {
            contentDescription = "添加待办"
            hint = "+  添加待办"
            textSize = todoSize()
            typeface = repository.font(context)
            setTextColor(colors.text)
            setHintTextColor(colors.weak)
            background = PaperTheme.rounded((colors.tint and 0x00FFFFFF) or 0x0C000000, colors.border, context.dp(11).toFloat())
            setPadding(context.dp(13), context.dp(10), context.dp(13), context.dp(10))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES or InputType.TYPE_TEXT_FLAG_MULTI_LINE
            imeOptions = EditorInfo.IME_ACTION_DONE
            minHeight = context.dp(48)
            maxLines = 4
            setOnTouchListener { _, event -> if (event.action == MotionEvent.ACTION_DOWN) prepareEditor(this); false }
            setOnEditorActionListener { _, action, _ ->
                if (action == EditorInfo.IME_ACTION_DONE) { commitTodo(this); true } else false
            }
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
                override fun afterTextChanged(text: Editable?) {
                    if (text?.contains('\n') == true) commitTodo(this@apply)
                }
            })
        }
        addView(newItem, LayoutParams(-1, -2).apply { setMargins(context.dp(10), context.dp(8), context.dp(10), context.dp(6)) })
    }

    private fun commitTodo(editor: EditText) {
        val text = editor.text.toString()
        if (text.isNotBlank()) repository.addTodo(paperId, text)
        editor.text.clear()
    }

    private fun buildNote(content: String) {
        note = MarkdownEditText(context).apply {
            contentDescription = "笔记正文"
            this.colors = this@PaperView.colors
            renderMode = repository.settingString("markdownRenderMode", "enhanced")
            imageReferenceMode = repository.settingString("imageReferenceTextMode", "always")
            imageResolver = { repository.imageStore.file(it) }
            textSize = noteSize()
            typeface = repository.font(context)
            setTypeface(typeface, if (repository.settingBoolean("noteTextBold", false)) android.graphics.Typeface.BOLD else android.graphics.Typeface.NORMAL)
            setPadding(context.dp(15), context.dp(13), context.dp(15), context.dp(18))
            gravity = Gravity.TOP or Gravity.START
            background = null
            setText(content)
            hint = "在这张纸上写点什么…"
            setHintTextColor(this@PaperView.colors.weak)
            setOnTouchListener { _, event -> if (event.action == MotionEvent.ACTION_DOWN) prepareEditor(this); false }
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
                override fun afterTextChanged(text: Editable?) {
                    if (!refreshing) repository.updateContent(paperId, text.toString())
                }
            })
        }
        addView(note, LayoutParams(-1, 0, 1f))
        val format = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(context.dp(7), 0, context.dp(7), 0) }
        format.addView(textButton(context, "B", "加粗", colors.active) { wrapSelection("**", "**") })
        format.addView(textButton(context, "I", "斜体", colors.active) { wrapSelection("*", "*") })
        format.addView(textButton(context, "↗", "插入链接", colors.active) { insertLink() })
        format.addView(textButton(context, "▧", "插入图片", colors.active) { onInsertImage(paperId) })
        format.addView(textButton(context, "MD", "导出或分享 Markdown", colors.active) { onShareNote(paperId) })
        format.addView(textButton(context, "Aa", "调整笔记字号", colors.active) { cycleNoteSize() })
        addView(format, LayoutParams(-1, context.dp(44)))
    }

    private fun buildFooter(type: String) {
        val footer = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(context.dp(5), 0, 0, 0) }
        undoButton = textButton(context, "↶", "撤销", colors.active) { repository.undo(paperId) }
        redoButton = textButton(context, "↷", "重做", colors.active) { repository.redo(paperId) }
        footer.addView(undoButton, LayoutParams(context.dp(42), -1))
        footer.addView(redoButton, LayoutParams(context.dp(42), -1))
        counter = TextView(context).apply {
            textSize = 11f; setTextColor(colors.weak); gravity = Gravity.CENTER_VERTICAL
            setOnClickListener { repository.lastSaveError?.let { error ->
                AlertDialog.Builder(context).setTitle("自动保存暂未完成").setMessage(error)
                    .setNegativeButton("关闭", null).setPositiveButton("重试") { _, _ -> runCatching { repository.flush() } }.create().showFor(context)
            } }
        }
        footer.addView(counter, LayoutParams(0, -1, 1f))
        if (type == "todo") footer.addView(textButton(context, "✓", "清理已完成", colors.active) { clearDone() }, LayoutParams(context.dp(40), -1))
        footer.addView(textButton(context, "⋯", "纸片菜单", colors.active) { paperMenu(it = footer) }, LayoutParams(context.dp(42), -1))
        if (onResize != null) {
            val grip = TextView(context).apply { text = "⠿"; textSize = 17f; gravity = Gravity.BOTTOM or Gravity.END; setTextColor(colors.weak); contentDescription = "拖动调整纸片大小" }
            installDrag(grip, resize = true) { dx, dy -> onResize?.invoke(dx, dy) }
            footer.addView(grip, LayoutParams(context.dp(30), -1))
        }
        addView(footer, LayoutParams(-1, context.dp(42)))
    }

    private fun paperMenu(it: View) {
        val popup = PopupMenu(context, it)
        popup.menu.add("选择纸片").setOnMenuItemClickListener { onChoosePaper(); true }
        popup.menu.add("重命名").setOnMenuItemClickListener { rename(); true }
        popup.menu.add("设置与数据迁移").setOnMenuItemClickListener { onSettings(); true }
        popup.menu.add("删除这张纸").setOnMenuItemClickListener {
            AlertDialog.Builder(context).setTitle("删除这张纸？").setMessage("纸片及其中的内容会被删除。导入的数据仍可从导入前备份恢复。")
                .setNegativeButton("取消", null).setPositiveButton("删除") { _, _ ->
                    repository.deletePaper(paperId)
                    repository.papers.firstOrNull()?.let { onOpenPaper(it.id) } ?: onNewPaper("todo")
                }.create().showFor(context)
            true
        }
        popup.show()
    }

    private fun rename() {
        val input = EditText(context).apply { setText(repository.paper(paperId)?.title); setSingleLine(); selectAll(); setPadding(context.dp(20), context.dp(12), context.dp(20), context.dp(12)) }
        AlertDialog.Builder(context).setTitle("纸片标题").setView(input)
            .setNegativeButton("取消", null).setPositiveButton("保存") { _, _ -> repository.updateTitle(paperId, input.text.toString()) }
            .create().showFor(context).window?.setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE)
        input.requestFocus()
    }

    private fun clearDone() {
        val done = repository.paper(paperId)?.items?.filter { it.done }.orEmpty()
        if (done.isEmpty()) { Toast.makeText(context, "没有已完成事项", Toast.LENGTH_SHORT).show(); return }
        AlertDialog.Builder(context).setTitle("清理 ${done.size} 项已完成事项？")
            .setNegativeButton("取消", null).setPositiveButton("清理") { _, _ -> repository.clearCompletedTodos(paperId) }
            .create().showFor(context)
    }

    private fun wrapSelection(before: String, after: String) {
        val editor = note ?: return
        prepareEditor(editor)
        editor.requestFocus()
        val start = minOf(editor.selectionStart, editor.selectionEnd).coerceAtLeast(0)
        val end = maxOf(editor.selectionStart, editor.selectionEnd).coerceAtLeast(start)
        val selection = editor.text.subSequence(start, end).toString()
        editor.text.replace(start, end, before + selection + after)
        editor.setSelection(start + before.length, start + before.length + selection.length)
    }

    private fun insertLink() {
        val input = EditText(context).apply { hint = "https://"; inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI; setPadding(context.dp(20), context.dp(12), context.dp(20), context.dp(12)) }
        AlertDialog.Builder(context).setTitle("插入链接").setView(input).setNegativeButton("取消", null)
            .setPositiveButton("插入") { _, _ ->
                val value = input.text.toString().trim()
                if (value.startsWith("https://") || value.startsWith("http://")) wrapSelection("[", "]($value)")
                else Toast.makeText(context, "请输入 http 或 https 链接", Toast.LENGTH_SHORT).show()
            }.create().showFor(context)
    }

    private fun cycleNoteSize() {
        val sizes = arrayOf("small", "medium", "large")
        val current = sizes.indexOf(repository.settingString("noteTextSize", "medium"))
        repository.setSetting("noteTextSize", sizes[(current + 1) % sizes.size])
    }

    private fun todoSize() = when (repository.settingString("todoVisualSize", "medium")) { "small" -> 14f; "large" -> 19f; else -> 16f } * repository.settingDouble("zoom", 1.0).toFloat()
    private fun noteSize() = when (repository.settingString("noteTextSize", "medium")) { "small" -> 14f; "large" -> 20f; else -> 16f } * repository.settingDouble("zoom", 1.0).toFloat()

    private fun prepareEditor(editor: EditText) {
        onRequestEditing()
        clearPendingIme()
        editor.post {
            if (!editor.isAttachedToWindow) return@post
            editor.requestFocus()
            fun showIme() = editor.post {
                if (editor.hasFocus() && editor.hasWindowFocus()) {
                    if (android.os.Build.VERSION.SDK_INT >= 30) editor.windowInsetsController?.show(android.view.WindowInsets.Type.ime())
                    else (context.getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT)
                }
            }
            if (editor.hasWindowFocus()) showIme()
            else {
                pendingImeEditor = editor
                pendingImeFocus = android.view.ViewTreeObserver.OnWindowFocusChangeListener { focused ->
                    if (focused) { clearPendingIme(); showIme() }
                }
                editor.viewTreeObserver.addOnWindowFocusChangeListener(pendingImeFocus)
            }
        }
    }

    private fun clearPendingIme() {
        val observer = pendingImeEditor?.viewTreeObserver
        pendingImeFocus?.let { if (observer?.isAlive == true) observer.removeOnWindowFocusChangeListener(it) }
        pendingImeEditor = null; pendingImeFocus = null
    }

    private fun installDrag(view: View, resize: Boolean = false, action: (Float, Float) -> Unit) {
        var x = 0f; var y = 0f; var moved = false
        view.setOnTouchListener { _, event ->
            if ((!resize && onDrag == null) || (resize && onResize == null)) return@setOnTouchListener false
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> { x = event.rawX; y = event.rawY; moved = false; true }
                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - x; val dy = event.rawY - y
                    if (moved || kotlin.math.abs(dx) + kotlin.math.abs(dy) > context.dp(5)) { moved = true; action(dx, dy); x = event.rawX; y = event.rawY }
                    true
                }
                MotionEvent.ACTION_UP -> { if (!moved) view.performClick(); true }
                MotionEvent.ACTION_CANCEL -> true
                else -> false
            }
        }
    }

    private inner class TodoHolder(val row: LinearLayout, val check: CheckBox, val editor: EditText, val menu: TextView, val drag: TextView, val link: TextView) : RecyclerView.ViewHolder(row) {
        var boundId = ""
        var binding = false
    }

    private inner class TodoAdapter : RecyclerView.Adapter<TodoHolder>() {
        var items: List<TodoItem> = emptyList()
        var dragging = false
        lateinit var touch: ItemTouchHelper

        fun submit(next: List<TodoItem>) {
            if (dragging) return
            if (items.map { it.id } != next.map { it.id }) { items = next; notifyDataSetChanged(); return }
            val old = items
            items = next
            next.forEachIndexed { index, item -> if (old[index] != item) notifyItemChanged(index) }
        }

        fun move(from: Int, to: Int) {
            dragging = true
            val next = items.toMutableList()
            next.add(to, next.removeAt(from))
            items = next
            notifyItemMoved(from, to)
        }

        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): TodoHolder {
            val row = LinearLayout(context).apply { orientation = VERTICAL; layoutParams = RecyclerView.LayoutParams(-1, -2) }
            val line = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL; minimumHeight = context.dp(50) }
            row.addView(line)
            val check = CheckBox(context).apply { buttonTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(colors.active, colors.checkbox)); minWidth = 0; minimumWidth = 0; setPadding(context.dp(5), 0, 0, 0) }
            line.addView(check, LayoutParams(context.dp(44), context.dp(48)))
            val editor = EditText(context).apply {
                textSize = todoSize(); setTextColor(colors.text); background = null
                typeface = repository.font(context)
                setPadding(context.dp(2), context.dp(8), context.dp(2), context.dp(8))
                inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
                minHeight = context.dp(48)
                setOnTouchListener { _, event -> if (event.action == MotionEvent.ACTION_DOWN) prepareEditor(this); false }
            }
            line.addView(editor, LayoutParams(0, -2, 1f))
            val menu = textButton(context, "⋮", "待办操作", colors.weak) {}
            line.addView(menu, LayoutParams(context.dp(32), context.dp(48)))
            val drag = textButton(context, "≡", "拖动排序", colors.weak) {}
            line.addView(drag, LayoutParams(context.dp(34), context.dp(48)))
            val link = TextView(context).apply { textSize = 12f; setTextColor(colors.link); setPadding(context.dp(48), 0, context.dp(12), context.dp(8)); minHeight = context.dp(32) }
            row.addView(link, LayoutParams(-1, -2))
            val holder = TodoHolder(row, check, editor, menu, drag, link)
            check.setOnCheckedChangeListener { _, done -> if (!holder.binding && holder.boundId.isNotEmpty()) repository.setTodoDone(paperId, holder.boundId, done) }
            editor.addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
                override fun afterTextChanged(text: Editable?) { if (!holder.binding && holder.boundId.isNotEmpty()) repository.updateTodoText(paperId, holder.boundId, text.toString()) }
            })
            menu.setOnClickListener { todoMenu(holder) }
            drag.setOnTouchListener { _, event ->
                if (event.actionMasked == MotionEvent.ACTION_DOWN) { hideKeyboard(); dragging = true; touch.startDrag(holder) }
                false
            }
            return holder
        }

        override fun onBindViewHolder(holder: TodoHolder, position: Int) {
            val item = items[position]
            holder.binding = true
            holder.boundId = item.id
            holder.check.isChecked = item.done
            holder.check.contentDescription = if (item.done) "标记为未完成" else "标记为完成"
            if (holder.editor.text.toString() != item.text) {
                val selection = holder.editor.selectionStart.coerceIn(0, item.text.length)
                holder.editor.setText(item.text)
                holder.editor.setSelection(selection)
            }
            holder.editor.contentDescription = "待办内容"
            holder.editor.setTextColor(if (item.done) colors.weak else colors.text)
            holder.editor.paintFlags = if (item.done) holder.editor.paintFlags or Paint.STRIKE_THRU_TEXT_FLAG else holder.editor.paintFlags and Paint.STRIKE_THRU_TEXT_FLAG.inv()
            holder.editor.setTypeface(repository.font(context), if (repository.settingBoolean("todoTextBold", false)) android.graphics.Typeface.BOLD else android.graphics.Typeface.NORMAL)
            val target = item.linkedNoteId?.let { repository.paper(it) }
            holder.link.visibility = if (target != null || !item.linkedPath.isNullOrBlank()) VISIBLE else GONE
            holder.link.text = if (target != null) "↗ ${target.title}" else "↗ ${item.linkedPath} · 电脑文件"
            holder.link.setOnClickListener {
                if (target != null) onOpenPaper(target.id) else Toast.makeText(context, "这是电脑上的文件路径，请在电脑端打开", Toast.LENGTH_LONG).show()
            }
            holder.binding = false
        }

        private fun todoMenu(holder: TodoHolder) {
            val popup = PopupMenu(context, holder.menu)
            popup.menu.add("关联纸片").setOnMenuItemClickListener {
                val papers = repository.papers.filter { it.id != paperId }
                if (papers.isEmpty()) Toast.makeText(context, "先新建另一张纸片", Toast.LENGTH_SHORT).show()
                else AlertDialog.Builder(context).setTitle("关联到纸片").setItems(papers.map { it.title }.toTypedArray()) { _, index -> repository.linkTodo(paperId, holder.boundId, papers[index].id) }.create().showFor(context)
                true
            }
            popup.menu.add("取消关联").setOnMenuItemClickListener { repository.linkTodo(paperId, holder.boundId, null); true }
            popup.menu.add("删除事项").setOnMenuItemClickListener { repository.deleteTodo(paperId, holder.boundId); true }
            popup.show()
        }
    }
}
