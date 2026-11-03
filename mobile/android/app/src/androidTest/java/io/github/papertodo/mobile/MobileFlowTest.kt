package io.github.papertodo.mobile

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Rect
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.provider.Settings
import android.text.style.StyleSpan
import android.view.View
import android.view.InputDevice
import android.view.MotionEvent
import android.view.accessibility.AccessibilityNodeInfo
import android.accessibilityservice.AccessibilityServiceInfo
import android.widget.EditText
import androidx.test.espresso.Espresso.onView
import androidx.test.espresso.UiController
import androidx.test.espresso.ViewAction
import androidx.test.espresso.action.ViewActions.*
import androidx.test.espresso.assertion.ViewAssertions.matches
import androidx.test.espresso.matcher.ViewMatchers.*
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.rule.ActivityTestRule
import io.github.papertodo.mobile.data.PaperRepository
import io.github.papertodo.mobile.overlay.OverlayService
import org.hamcrest.Matcher
import org.hamcrest.Matchers.allOf
import org.junit.After
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import java.io.File

/** End-to-end flows only run on an emulator and only create/delete their own papers. */
@Suppress("DEPRECATION")
class MobileFlowTest {
    @get:Rule val activityRule = ActivityTestRule(MainActivity::class.java, false, false)
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private lateinit var context: Context
    private lateinit var repository: PaperRepository
    private val created = mutableListOf<String>()
    private var originalSelected: String? = null
    private var originalAutoClear = false
    private var originalAutoMove = false
    private var overlayWasAllowed = false
    private var notificationWasAllowed = false
    private var prepared = false
    private var originalHardwareImeSetting: String? = null

    @Before fun prepareOnlySyntheticDataOnEmulator() {
        assumeTrue("MobileFlowTest must never change data on a real phone",
            Build.HARDWARE.contains("ranchu") || Build.HARDWARE.contains("goldfish"))
        context = instrumentation.targetContext
        repository = (context.applicationContext as PaperApp).repository
        assumeTrue("Keep an existing unreadable database intact", !repository.isReadOnly)
        originalSelected = context.getSharedPreferences("mobile-ui", Context.MODE_PRIVATE).getString("selected", null)
        overlayWasAllowed = Settings.canDrawOverlays(context)
        notificationWasAllowed = Build.VERSION.SDK_INT < 33 ||
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        prepared = true
        instrumentation.runOnMainSync {
            OverlayService.stop(context)
            originalAutoClear = repository.settingBoolean("autoClearCompletedTodos", false)
            originalAutoMove = repository.settingBoolean("autoMoveCompletedTodosToBottom", false)
            repository.setSetting("autoClearCompletedTodos", false)
            repository.setSetting("autoMoveCompletedTodosToBottom", false)
        }
        await { !OverlayService.isRunning }
    }

    @After fun removeOnlyPapersCreatedByThisTest() {
        if (!prepared || !::context.isInitialized || !::repository.isInitialized) return
        runCatching { activityRule.finishActivity() }
        instrumentation.runOnMainSync {
            OverlayService.stop(context)
            created.forEach { id -> if (repository.paper(id) != null) repository.deletePaper(id) }
            repository.setSetting("autoClearCompletedTodos", originalAutoClear)
            repository.setSetting("autoMoveCompletedTodosToBottom", originalAutoMove)
            val preferences = context.getSharedPreferences("mobile-ui", Context.MODE_PRIVATE).edit()
            if (originalSelected == null) preferences.remove("selected") else preferences.putString("selected", originalSelected)
            preferences.commit()
        }
        repository.flush()
        await { !OverlayService.isRunning }
        if (!overlayWasAllowed) shell("appops set ${context.packageName} SYSTEM_ALERT_WINDOW default")
        if (Build.VERSION.SDK_INT >= 33 && !notificationWasAllowed) {
            shell("pm revoke ${context.packageName} android.permission.POST_NOTIFICATIONS")
        }
        originalHardwareImeSetting?.let { value ->
            if (value == "0" || value == "1") shell("settings put secure show_ime_with_hard_keyboard $value")
            else shell("settings delete secure show_ime_with_hard_keyboard")
        }
    }

    @Test fun todoMultilinePasteCompletionAndUndoWorkThroughTheRealActivity() {
        val id = createPaper("todo", "验证待办")
        launch(id)
        onView(withContentDescription("添加待办"))
            .perform(replaceText("整理今天的重点\n阅读三十分钟\n"), closeSoftKeyboard())
        await { repository.paper(id)?.items?.size == 2 }
        onView(allOf(
            withContentDescription("标记为完成"),
            withParent(hasDescendant(withText("整理今天的重点"))),
        )).perform(click())
        await { repository.paper(id)?.items?.firstOrNull()?.done == true }
        capture("todo-completed")
        onView(withContentDescription("撤销")).perform(click())
        await { repository.paper(id)?.items?.size == 2 && repository.paper(id)?.items?.all { !it.done } == true }
        onView(withContentDescription("撤销")).perform(click())
        await { repository.paper(id)?.items?.isEmpty() == true }
        assertNotNull(repository.paper(id))
    }

    @Test fun chineseMarkdownSurvivesSwitchingAwayAndBack() {
        val noteId = createPaper("note", "验证笔记")
        val todoId = createPaper("todo", "切换验证")
        val source = "# 一张安静的纸\n\n把重要的事写下来。\n\n**专注当下**，慢慢完成。\n\n" +
            "- 阅读三十分钟\n- 整理今天的想法\n\n> 做一点，就前进一点。\n"
        launch(noteId)
        onView(withContentDescription("笔记正文")).perform(replaceText(source), closeSoftKeyboard(), clearNativeFocus())
        await { repository.paper(noteId)?.content == source }
        var noteEditor: EditText? = null
        onView(withContentDescription("笔记正文")).check { view, error ->
            if (error != null) throw error
            noteEditor = view as EditText
        }
        await { noteEditor?.text?.getSpans(0, noteEditor!!.length(), StyleSpan::class.java)?.isNotEmpty() == true }
        capture("note-markdown")
        onView(withText("☷")).perform(click())
        onView(withText("✓  ${repository.paper(todoId)!!.title}")).perform(click())
        onView(withContentDescription("添加待办")).check(matches(isDisplayed()))
        onView(withText("☷")).perform(click())
        onView(withText("✎  ${repository.paper(noteId)!!.title}")).perform(click())
        onView(withContentDescription("笔记正文")).check(matches(withText(source)))
        assertEquals(source, repository.paper(noteId)?.content)
    }

    @Test fun foregroundOverlaySwitchesPapersCollapsesToCapsulesAndCleansUp() {
        val first = createPaper("todo", "悬浮待办")
        val second = createPaper("note", "悬浮笔记")
        instrumentation.runOnMainSync {
            repository.addTodo(first, "保持纸片轻巧")
            repository.updateContent(second, "# 随手记\n\n切换纸片，内容仍在。")
        }
        shell("appops set ${context.packageName} SYSTEM_ALERT_WINDOW allow")
        if (Build.VERSION.SDK_INT >= 33) shell("pm grant ${context.packageName} android.permission.POST_NOTIFICATIONS")
        await { Settings.canDrawOverlays(context) }
        launch(first)
        instrumentation.runOnMainSync {
            OverlayService.start(activityRule.activity, first)
            activityRule.activity.moveTaskToBack(true)
        }
        await { OverlayService.isRunning && OverlayService.activePaperId == first }
        awaitShell { overlayWindowCount() >= 2 }
        assertTrue(shell("dumpsys activity services ${context.packageName}").contains("isForeground=true"))
        capture("overlay-expanded")

        originalHardwareImeSetting = shell("settings get secure show_ime_with_hard_keyboard").trim()
        shell("settings put secure show_ime_with_hard_keyboard 1")
        tapOverlayTodoInput()
        awaitShell { shell("dumpsys input_method").contains("mInputShown=true") }
        if (Build.VERSION.SDK_INT >= 30) {
            awaitShell {
                val imeTop = imeTop()
                val paperBottom = paperWindowBottom()
                imeTop != null && paperBottom != null && paperBottom <= imeTop
            }
        }
        capture("overlay-keyboard")
        shell("input keyevent 4")
        awaitShell { !shell("dumpsys input_method").contains("mInputShown=true") }

        instrumentation.runOnMainSync { OverlayService.start(context, second) }
        await { OverlayService.activePaperId == second }
        val expandedCount = overlayWindowCount()
        instrumentation.runOnMainSync { OverlayService.collapse(context) }
        await { OverlayService.isRunning && OverlayService.activePaperId == null }
        awaitShell { overlayWindowCount() == expandedCount - 1 }
        capture("capsules-only")
        assertTrue("Capsules should remain while foreground service is enabled", overlayWindowCount() > 0)

        instrumentation.runOnMainSync { OverlayService.stop(context) }
        await { !OverlayService.isRunning }
        awaitShell { overlayWindowCount() == 0 }
        assertEquals("# 随手记\n\n切换纸片，内容仍在。", repository.paper(second)?.content)
    }

    private fun createPaper(type: String, title: String): String {
        var id = ""
        instrumentation.runOnMainSync {
            id = repository.createPaper(type)
            created += id
            repository.updateTitle(id, "$title ${id.take(6)}")
        }
        return id
    }

    private fun launch(id: String) {
        activityRule.launchActivity(Intent(context, MainActivity::class.java)
            .putExtra(OverlayService.EXTRA_PAPER, id))
        instrumentation.waitForIdleSync()
    }

    private fun clearNativeFocus(): ViewAction = object : ViewAction {
        override fun getConstraints(): Matcher<View> = isAssignableFrom(EditText::class.java)
        override fun getDescription() = "Leave native Markdown editor in browsing state"
        override fun perform(uiController: UiController, view: View) {
            view.clearFocus()
            (view.parent as? View)?.requestFocus()
            uiController.loopMainThreadUntilIdle()
        }
    }

    /** adb pull /sdcard/Android/data/io.github.papertodo.mobile/files/verification/ */
    private fun capture(name: String) {
        instrumentation.waitForIdleSync()
        SystemClock.sleep(180)
        val directory = context.getExternalFilesDir("verification") ?: return
        check(directory.exists() || directory.mkdirs())
        val image = instrumentation.uiAutomation.takeScreenshot() ?: error("No screenshot returned")
        File(directory, "$name-api36.png").outputStream().use {
            check(image.compress(Bitmap.CompressFormat.PNG, 100, it))
        }
        image.recycle()
    }

    private fun overlayWindowCount(): Int = Regex(
        "package=${Regex.escape(context.packageName)} appop=SYSTEM_ALERT_WINDOW"
    ).findAll(shell("dumpsys window windows")).count()

    private fun imeTop(): Int? = Regex(
        "type=ime frame=\\[-?[0-9]+,(-?[0-9]+)\\]\\[[^\\]]+\\][^\\r\\n]*visible=true"
    ).find(shell("dumpsys window displays"))?.groupValues?.get(1)?.toIntOrNull()

    private fun paperWindowBottom(): Int? {
        val windows = shell("dumpsys window windows").split(Regex("(?m)(?=^  Window #[0-9]+ )"))
        val paper = windows.firstOrNull {
            it.contains("package=${context.packageName} appop=SYSTEM_ALERT_WINDOW") &&
                Regex("mAttrs=\\{\\([^)]*\\)\\([0-9]+x[0-9]+\\)").containsMatchIn(it)
        } ?: return null
        return Regex("Frames:[^\\r\\n]* frame=\\[-?[0-9]+,-?[0-9]+\\]\\[-?[0-9]+,(-?[0-9]+)\\]")
            .find(paper)?.groupValues?.get(1)?.toIntOrNull()
    }

    /** Espresso requires a resumed Activity; an overlay deliberately has none. */
    private fun tapOverlayTodoInput() {
        val automation = instrumentation.uiAutomation
        val info = automation.serviceInfo
        val previousFlags = info.flags
        info.flags = info.flags or AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS
        automation.serviceInfo = info
        try {
            fun findInput(node: AccessibilityNodeInfo): Rect? {
                if (node.contentDescription?.toString() == "添加待办" &&
                    node.packageName?.toString() == context.packageName) {
                    return Rect().also(node::getBoundsInScreen).takeIf { !it.isEmpty }
                }
                for (index in 0 until node.childCount) {
                    val child = node.getChild(index) ?: continue
                    val result = findInput(child)
                    @Suppress("DEPRECATION") child.recycle()
                    if (result != null) return result
                }
                return null
            }
            var bounds: Rect? = null
            val deadline = SystemClock.uptimeMillis() + 4000
            while (bounds == null && SystemClock.uptimeMillis() < deadline) {
                for (window in automation.windows) {
                    val root = window.root ?: continue
                    bounds = findInput(root)
                    @Suppress("DEPRECATION") root.recycle()
                    if (bounds != null) break
                }
                if (bounds == null) SystemClock.sleep(50)
            }
            val target = bounds ?: error("Floating todo input was not exposed to accessibility")
            val now = SystemClock.uptimeMillis()
            val down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN,
                target.exactCenterX(), target.exactCenterY(), 0).apply { source = InputDevice.SOURCE_TOUCHSCREEN }
            val up = MotionEvent.obtain(now, now + 60, MotionEvent.ACTION_UP,
                target.exactCenterX(), target.exactCenterY(), 0).apply { source = InputDevice.SOURCE_TOUCHSCREEN }
            try {
                check(automation.injectInputEvent(down, true))
                SystemClock.sleep(60)
                check(automation.injectInputEvent(up, true))
            } finally { down.recycle(); up.recycle() }
        } finally {
            val restored = automation.serviceInfo
            restored.flags = previousFlags
            automation.serviceInfo = restored
        }
    }

    private fun shell(command: String): String =
        ParcelFileDescriptor.AutoCloseInputStream(instrumentation.uiAutomation.executeShellCommand(command))
            .bufferedReader().use { it.readText() }

    private fun await(condition: () -> Boolean) {
        val deadline = SystemClock.uptimeMillis() + 7000
        while (SystemClock.uptimeMillis() < deadline) {
            var ready = false
            instrumentation.runOnMainSync { ready = condition() }
            if (ready) return
            SystemClock.sleep(30)
        }
        fail("Activity/service state did not settle within 7 seconds")
    }

    private fun awaitShell(condition: () -> Boolean) {
        val deadline = SystemClock.uptimeMillis() + 7000
        while (SystemClock.uptimeMillis() < deadline) {
            if (condition()) return
            SystemClock.sleep(60)
        }
        fail("Overlay window state did not settle within 7 seconds")
    }
}
