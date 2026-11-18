# PaperTodo Changelog

[简体中文](CHANGELOG.zh.md)

> **Related Links**: [Back to Home](README.md) · [User Manual](doc/USER_GUIDE.en.md)

This log is written for general and power users alike. It focuses on user-facing features, behavioral changes, enhancements, and fixes.

---

### Planned / To-Do

- None currently.

### Under Evaluation

- **Scoop / Chocolatey Package Managers**: Scoop is a great fit for portable, green installations to simplify install and update flows; Chocolatey is more system-level. Release automation, package manifest maintenance, checksum verification, and user data retention need to be evaluated before adoption.

---

### Unreleased

### v4.0.0-beta2

#### Workspaces

- **Create and switch workspaces.** Keep up to five independent workspaces, each with its own papers, images and plugin state.
  - In **Spaces & Data**, you can create, rename or delete secondary workspaces. By default, they inherit the main workspace’s app settings.
  - Switch workspaces or create one from the title-bar shortcut, the right-click menu or the Workspaces section of the tray menu.
  - To move a paper, right-click it and choose **Move to**. Its contents, images and plugin data move with it.

#### Backups & Recovery

- **Recover unreadable workspace data.** PaperTodo uses local and historical backups to reduce the risk of losing your work.
  - If workspace data cannot be read, PaperTodo tries the local backup automatically or offers a recent valid historical backup.
  - After you restore an older main configuration, PaperTodo scans for missing secondary workspaces. If their local data or backups are valid, they reappear under their original numbers. Unrecognized data is preserved when you create a workspace.
  - After startup, PaperTodo checks every 10 minutes and keeps up to four ZIP archives in `PaperTodo-backups/` for manual recovery. The restore points span roughly 10 minutes, 6 hours, 3 days and 1 month.

#### Edge Previews & Capsules

- **Free capsule queues.** With free queues enabled, drag a single edge capsule out to start a separate group. Hold `Ctrl` while dragging a capsule, or drag the group’s master capsule, to move the whole group.
  - Other capsules slide aside behind the one you’re dragging. When you collapse or expand a queue, its members slide under or out from beneath the master capsule.
- **Move a queue between monitors.** Drag the master capsule horizontally to move the whole queue to the opposite edge of the screen or to another monitor.
- **Preview where a capsule will land.** When you reorder, insert or merge capsules, neighboring capsules slide aside and an outline marks the drop position.
- **Fewer accidental previews.** After a drag, the preview waits for the pointer to settle. Moving the pointer away no longer opens the capsule you just moved.
- **Smoother paper resizing.** The default skin uses precomputed shadows to make edge resizing smoother and reduce resource use.
- Todo and note hover previews now share a maximum height of 320 DIP. Shorter previews fit their contents.
- Under **General → Advanced**, you can choose a compact master capsule that shows only an arrow and hides the queue count.

#### Todos & Markdown

- **Multiline todos and folding.** Press `Shift+Enter` for a new line within a todo, or `Enter` to start the next todo. For items longer than four lines, click the arrow on the left to fold them to a two-line preview. Search highlights matches in folded content.
- **Work with multiple todos.** Drag a selection box around several todos, then press `Backspace` to delete them together, with undo and redo support. Drag selected todos as a group to keep their order. The list scrolls as you drag near its top or bottom edge.
- **Export notes and todos.** The paper menu’s **Export** submenu lets you export the current paper, all notes, all todos, or both notes and todos together.
  - Choose Markdown or HTML. A single HTML export embeds its images, and a bulk export produces a ZIP archive.
- **Open notes in an external app.** PaperTodo opens a note in the associated Windows app and falls back to Notepad if there’s no association.
- You can hide the quick-add bar at the bottom of todo papers. Hiding it also turns off drag-to-trash deletion.

#### Appearance, Settings & Interaction

- **Seven experimental skins.** Choose from the default Paper skin, Mica, two Acrylic styles, Tracing Paper, Aero and Pixel. Each skin has its own color settings.
  - **Native Windows materials.** On Windows 11 22H2 or later, you can enable native backdrops for expanded papers and Settings. Restart PaperTodo the first time you enable them. On 24H2 or later, the backdrop can extend through the title bar.
  - **Five transparency levels.** Choose Very Low, Low, Medium, High or Very High to adjust the background while keeping text crisp. Capsules and context menus use a lighter material for readability.
  - Drag any edge or corner to resize a paper in any skin. The dotted grip in the lower-right corner has been removed.
- **More consistent icons.** Title-bar and menu actions now use consistent strokes and proportions.
- **Title-bar shortcuts.** The new `···` button opens the same shortcut menu as right-clicking the paper.
  - Under **Settings → General → Title-bar Shortcuts**, drag to place or reorder Associate by Drag, Switch Workspace, New Note, New Todo, External Editor and Run Script. You can hide each action individually.
  - When a paper is too narrow for the shortcuts, they move into the menu as a group and appear only once.
- **Run Script shortcut.** Save a multiline PowerShell script and run it with one click from the title bar or menu.
- **Reorganized settings.** The sidebar lists General, Notes & Todos, Style & Text and Shortcuts, followed by Spaces & Data, Plugins and Labs below a divider. Notes and Todos share a two-column page. Each column has its own restore-defaults action.
- **External window links.** Double-click the title-bar link icon to unlink a window. Right-click to bring the target app forward or launch it. PaperTodo resumes following the app when it restarts.
- **Clearer active edge capsules.** In dark mode, a brighter active outline and a thin translucent outer border make the capsule easier to see against a similarly dark desktop.
- With **Stretch** on, custom background images fill the paper while preserving their aspect ratio. With it off, large images scale down to fit and small images keep their original size.
- **Fast Animations** in General settings shortens visual transitions to 60% of their usual duration.

#### Plugins, MCP & Codex

- **Plugin API 2.3.** Plugins targeting 2.1 or 2.2 remain compatible.
  - **Host controls.** Plugins can hide their papers at startup (`hidden`), add action buttons to Settings (`action`), put actions in the menu only (`menuOnly`), show, hide, expand or collapse other papers, and use a shared API for app settings.
  - **Todo reminders.** Plugins can receive an event about five seconds before a reminder is due and present it themselves, for example through a desktop pet. Otherwise, PaperTodo shows the usual reminder.
- **On-demand reload and three-way merge.** Plugins and MCP can reload the active data file with `reload_data` or supply the original JSON as a baseline for `merge_data`. Review conflicting fields side by side, or give internal or external changes priority. PaperTodo keeps the five most recent sets of conflict records.
- **Local MCP improvements.**
  - Missing or incorrectly typed arguments now identify the field at fault. Use `order` in `update_todo` to reorder todos. Reading a note includes unsaved edits.
  - A binary-stream import command lets local AI tools append images up to 4 MiB to a note.
- **Codex CLI Bridge.**
  - The default model is GPT-6 Astra (`gpt-6-astra`) with `xhigh` reasoning. Leave the model field blank to keep the CLI’s own model setting. Sending a task opens a foreground PowerShell window by default.
  - The interface and default prompts follow the app language. Your edited prompts are preserved. The prompt editor paper starts hidden and opens from **More Settings → Edit Prompt**.
  - Three independent switches, all on by default, control the plugin-development skill, the PaperTodo operation skill and whether the bridge can enable MCP.

#### Major Fixes & Improvements

- **More responsive todo papers.** Papers with many todos respond faster at startup, when expanded, during bulk paste or reordering, and when using Show All or Hide All.
- **Recover off-screen papers.** When connecting or disconnecting a monitor leaves an expanded paper entirely off-screen, PaperTodo brings it back onto the current screen. Clicking its active edge capsule can also retrieve it. At startup, PaperTodo briefly waits for an external monitor to become available before deciding a paper is off-screen.
- **Smoother transitions with native materials.** Papers prepare their background before expanding or collapsing, eliminating the transparent flash and clipped content at the start of the animation.
- **Mixed-language titles.** Halfwidth characters such as Latin letters and digits count as half a Chinese character. The default display limit is still six Chinese characters or the equivalent. Lowering the limit hides the extra characters and preserves the saved title.
- **Interactive website demo.** Try PaperTodo 4.0 papers, edge previews, Markdown and scripts on the website. The demo scales for high-resolution screens, remembers your language and supports dark mode.

#### Other Fixes & Improvements

- Disabling PaperTodo in Windows Startup Apps or Task Manager is now reflected in the app’s settings.
- Dragging or resizing a paper no longer triggers repeated full-screen avoidance checks, reducing overhead during those actions.
- On the default skin, enabling both **Hide outer border** and **Hide inactive title bar** now leaves the entire title-bar area transparent when the paper loses focus.
- A paper opened from an edge capsule now takes focus before the animation starts. Closing or hiding the active paper passes focus according to window stacking order, so a covered background window no longer jumps forward unexpectedly.
- Scheduled statistics saves now run one at a time in the background, keeping the interface responsive when the disk is busy.
- Launching a second instance now skips excessively long commands, allowing subsequent requests to open or bring PaperTodo forward to proceed.
- Removed **Clear Completed** from the todo context menu.
- Changing the paper title display limit immediately updates linked paper names in todos. Click a todo’s link icon again to collapse a linked paper even when it’s outside a capsule queue.
- Transparent images keep their transparency when copied and pasted into notes. Images also render correctly when a hidden linked note is reopened.
- Fixed an occasional mismatch between an edge preview’s position and its clickable area when moving quickly between capsules.
- Fixed unresponsive buttons in some Web plugin Mini views after the view was shown again.
- When several todo reminders are due at once and Windows cannot display a notification, reminders that were never shown are no longer marked as delivered.
- Papers now remember their expanded position and size when **Keep edge capsule visible while expanded** is off. Temporarily disabling capsules or hiding a queue preserves its starting height and collapsed state when restored.
- Corrupt plugin state or Review Archive data now produces an error and preserves the original file.
- Changing **Prefer PowerShell 7** or **Hide Run Window** affects future runs and lets active persistent scripts continue. Stopping a script by switching workspaces no longer reports a false failure.
- Updated WebView2 to `1.0.4314.35`, Microsoft.Extensions.Hosting to `10.0.14` and the MCP SDK to `2.3.1`.

### v4.0.0-beta1

#### Edge Preview & Capsules

- **Live edge previews**: Hover over an edge capsule to browse and interact with content without opening the full paper.
  - **Todo live preview**: Shows a simplified todo list with direct check / uncheck actions. Click the card background to open the full paper; linked-paper and quick-launch actions use stable dedicated click targets.
  - **Note live preview**: Supports up to 6000 characters and follows the note's Markdown rendering.
  - **Smoother handoff and intent prediction**: Adjustable pointer-intent prediction reduces accidental switching while browsing between capsules. “Prefer downward expansion while browsing” can be enabled; when enabled, downward switches expand downward whenever the target card still fits instead of filling the space above.
- **Repeat-click behavior**: Clicking an edge capsule again retracts its paper when the paper is still clearly visible; if the paper is substantially covered by another window, it is brought to the front instead of being collapsed out of sight.
- **More flexible edge-capsule titles**: Title length can be reduced further, including fully hiding the title.
- Todo and note capsule icons and titles now align consistently.
- Hiding the close button no longer leaves an empty slot.
- Master capsules use a more compact count-only presentation.

#### Plugin System & Desktop Micro-Apps

- **Desktop micro-app container**: Note papers can become independent desktop micro-apps such as Pomodoro timers, clocks, review pools or system monitors. Settings adds a dedicated Plugins center; plugin data is isolated under `plugins/data/` with 10 MB / 20 MB limits.
  - **Web / Native dual runtime architecture**:
    - **Web plugins**: Built with WebView2 and standard HTML/CSS/JS, with no compilation required.
    - **Native plugins**: Built with .NET 10 + WPF for fully custom, high-performance desktop UI.
  - **Deep host integration**:
    - **Custom capsules and Mini views**: Plugins can customize collapsed capsules with icons, text, dynamic progress bars / rings or fully custom WPF drawing, and provide lightweight Mini views for edge hover.
    - **Todo integration**: Plugins can add right-side todo action icons and context-menu actions.
    - **Top bar and keys**: Plugins can add top-bar actions and status labels, register dedicated global hotkeys, and exclusively handle `Esc` and body context menus.
    - **Paper menus and lightweight popups**: Plugins can add text actions to paper context menus, read permitted note images, and open small focus-loss-closing popups from clicked buttons or menu items.
    - **Internationalization**: Plugin manifests can provide localized text that follows the current UI language.
  - Security: there is no artificial security sandbox; install third-party plugins only from trusted sources.
  - Examples:
    - Example plugins include the Codex CLI Bridge, which can send todo or paper context to a local Codex CLI, plus native clocks, Pomodoro timers, review pools and Web clocks adapted to narrow windows, capsules and Mini views.
    - Full example source lives in `plugin-samples/`.
    - Ready-to-use plugins live in `plugins/`.

#### Todos & Markdown

- **Full-text search**: `Ctrl+F` searches all todos and notes, showing current-paper and global match counts. Use `Enter` / `Shift+Enter` or the navigation controls to move through matches across papers, with matching capsules opened automatically; the search bar can be dragged to reposition it.
- **WYSIWYG Markdown editing**: Full Render provides block-level Markdown rendering while editing, keeping the note visually rendered instead of switching to a separate plain-source view.
- **Markdown parsing and display consistency**: Headings, quotes, lists, code blocks, links, basic HTML, escapes and image-code boundaries now share the same Markdown semantics, reducing inconsistencies in complex nested content.
- **Todo multi-selection and batch actions**: Drag along the left side to select multiple todos, then batch check / uncheck, copy, delete from the context menu, or drag the group to the trash. Arrow-key editing can continue across items without racing through multiple todos on key repeat.
- **Todo reminders**: Set custom durations, preset intervals, this evening, tomorrow morning and other reminder times; due reminders locate and highlight the todo with a tray notification and sound.
- The paper limit increases from 100 to 200.
- `Ctrl+Shift+C` copies selected todos as Markdown tasks with completion states, or copies selected note text as plain text.
- Markdown supports bold-italic (`***text***` / `___text___`), combinations of bold / italic / strikethrough / links, and backslash escapes.

#### Important Fixes & Performance Improvements

- **Saving and input protection**: Improve saving reliability in extreme conditions, reduce backup frequency and check availability before updating.
- **Fullscreen avoidance**: Fix papers remaining above some administrator-elevated fullscreen apps in the foreground when fullscreen avoidance is enabled.
- **Faster startup and exit**: Parallel and queued startup / shutdown work reduces waiting and improves responsiveness.

#### Appearance, Settings & Interaction

- **Custom paper backgrounds**: Place `papertodo.png`, `papertodo.jpg` or `papertodo.jpeg` beside `PaperTodo.exe` to use a custom paper background.
  - Appearance settings can show the original image or blend it with paper colors. Stretch is a separate toggle; position can be Center / Bottom Left / Bottom Center / Bottom Right.
  - Decoding caps the longest edge at 4096 pixels.
- **Settings redesign**: Settings now uses left-side navigation and improved layout, adapting to the active work area on small screens, high scaling and cross-DPI moves. Switches and hotkey recording refresh locally to reduce flicker. Edge browsing settings now live in General instead of Experimental Labs.
- **Quick close for the active paper**: Expanded papers support `Ctrl+W`, and middle-clicking the title bar performs the same close / collapse action.

#### Experimental Labs Features

- **Local MCP**: Start with `--mcp` to expose a standard MCP service that lets authorized AI assistants read, create, append and manage PaperTodo notes / todos; Settings can copy the configuration and AI Skill prompt in one click.
- **External window tethering**: Drag the paper's top-bar tether control onto a third-party window to attach the paper and smoothly follow that window as it moves, minimizes and restores.
- **Magnetic edge capsules**: Floating capsules can snap near screen or external-window edges, slide out on hover and retract when the pointer leaves.
- **New global hotkeys**:
  - Lock all papers.
  - Toggle opacity for all papers or the active paper.
  - Toggle opacity for all capsules.
  - Send papers / capsules behind other windows.
- **Focus-loss automation**: Papers can fade, hide top-bar icons / the title bar, or collapse into capsules when focus is lost.
- **Resting translucency**: Normal and docked capsules can become translucent while idle, with separate opacity controls.
- Docked and master capsules can disable forced topmost behavior.

#### Other Fixes & Improvements

- Todo paste exceeding count / text limits and notes reaching the input-protection limit now show explicit notices instead of silently dropping or refusing input.
- Papers opened from capsules obtain foreground focus more reliably, avoiding keyboard input or `Ctrl+W` still going to the previous app.
- Fix “Restore defaults for this page” on Shortcuts failing to restore the “Distinguish numpad digits” option.
- Fix Settings dropdowns not fully following the current theme.
- Fix papers with “Remember expanded position” being pulled back to the capsule's monitor after collapsing when the paper and capsule are on different displays; newly remembered positions support mixed scaling.
- Fix edge capsules moved to a secondary monitor sometimes opening their paper back on the old monitor.
- Long-note edits update only affected Markdown ranges; multi-line fence changes track the actual range to avoid full reparsing or missed updates. Improve capsule animation, monitor switching and window tracking with high refresh rates and mixed-DPI displays.
- The Windows single-file package without .NET shrinks from about 33 MiB to 17 MiB.

---

### v3.31

**Bug fixes and improvements**

- **Reset shortcut settings correctly**: “Restore defaults for this page” now turns off “Distinguish numpad digits” as expected, so the number row and numpad continue to trigger the same shortcuts.
- **Preserve heading typography in Markdown**: Italic, strikethrough, and underlined text inside headings now retain the heading's bold weight and custom bold font, rather than only its font size.
- **Clearer advanced settings**: Advanced options now use subtly tinted backgrounds and grouped borders to make them easier to distinguish from common settings.
- **Fix repeated refreshes in notes with images**: Notes containing images no longer repeatedly refresh while idle in the foreground, avoiding sustained high CPU and GPU usage.
- **Faster resizing for notes with images**: Reuse displayed image content and reduce repeated refreshes and processing while resizing a paper.
- **Custom bold fonts in Markdown**: With enhanced bold enabled, Markdown headings, bold text, and `<b>` / `<strong>` elements correctly use the custom bold font.
- Fixed completed items losing their position at the bottom when new todos were added using the bottom ＋ button, Enter, or multi-line paste with “Move completed items to bottom” enabled.
- Fixed key recording with some input methods incorrectly recognizing keys and preventing global shortcuts from being saved or triggered.

---

### v3.3

**Todos and quick launch**

- **File and folder quick launch**: Drop a local file or folder onto a todo to link its path. Open it, reveal its location, or unlink it from the todo; invalid paths are clearly marked. A todo can link to either a path or a paper.
- **Link any paper**: Todos can now link to another todo paper as well as a note paper. Existing note links remain compatible.
- **Move completed items to bottom**: When enabled, checking an item moves it to the end of the completed section; unchecking it moves to the end of the active section. This behavior is suspended while automatic clearing is enabled.

**Notes and Markdown**

- **Clickable plain URLs**: Plain `http://` and `https://` URLs in note text can be opened directly. URLs in inline code and code blocks remain plain text, and trailing punctuation, brackets, and paired emphasis markers are excluded from the link.
- **Better rendering performance for notes with images**: Fixed small viewport changes causing continuous redraws and high GPU usage while a note was idle in the foreground.
- **Lower memory use when importing large images**: Images are decoded directly at the target size during import and compression, avoiding a full-resolution decode and reducing peak memory use.

**Capsules and appearance**

- **Adjustable edge capsule spacing**: Choose 0, 4, or 8 DIP in Appearance settings; the default remains 4 DIP.
- **Clearer shadows**: Refined shadows for expanded papers, floating capsules, capsules dragged out of a queue, and master/edge capsules to make their states easier to distinguish.

**Settings and system integration**

- **Interface language selection**: Choose System default, 简体中文, English, 日本語, or 한국어 in Settings. Changes take effect after saving and restarting. Fixed language changes not taking effect.
- **Numpad digit option**: Global shortcuts can distinguish numpad digits from the number row; by default, both trigger the same shortcut.

**Bug fixes and other changes**

- **Improved startup compatibility with older Windows versions**, avoiding startup failures in some environments.
- Fixed papers with “Remember expanded position” enabled sometimes opening in the wrong location when restored through global shortcuts, the tray's “Show all,” or visibility toggles.
- Improved batch restoration of multiple papers, reducing repeated layout work and refreshing link states together.
- Fixed note images not promptly updating their layout or decoding at the new DPI after changing Windows display scaling or moving between monitors.
- Reduced repeated refreshes in note preview and Markdown rendering modes.
- Fixed a paper's context menu sometimes flashing and immediately closing when first opened during note editing.
- Turning off capsule or edge capsule mode now preserves the “Show master capsule” preference for the next time the mode is enabled.
- Fixed edge capsule submenu items sometimes being closed prematurely by outside-click handling.
- Improved fullscreen avoidance: papers hidden from the taskbar or window switcher no longer remain above some borderless fullscreen apps.
- Fixed tray context menus appearing away from the pointer or closing on first use after changing display scaling or moving between monitors with different DPI.
- Added anonymous usage statistics to help improve future versions.
- Empty-text todos with a paper link or quick-launch path are retained, rather than silently removed by empty-item cleanup or Backspace.
- Restored keyboard access to todo context menus using the Menu key or `Shift+F10`.
- Reduced unnecessary recreation of todo context menus.

---

### v3.2.1

**Bug fixes and edge cases**

- Improved fullscreen avoidance: replaced frequent edge capsule topmost enforcement with foreground-window events, a lightweight 1-second check, and a 5-second fallback check. Disabling fullscreen avoidance also removes the related listeners and timers.
- Fixed a possible crash after restarting the app when deleting a non-empty paper directly from the context menu of an edge capsule that had not yet been expanded.
- Fixed a note's content context menu sometimes reverting to an old title or outdated actions after entering and leaving edit mode.

---

### v3.2

This release focuses on stability and refinements. Plugin support and other experimental features are not included.

**Improvements**

- **Resize handle options**: Choose one of three modes in Appearance settings. Standard shows an opaque dotted handle in the lower-right corner; Soft (the default) shows it at about 50% opacity; Hidden removes the dots while allowing resizing from any edge or corner.
- **Faster startup**: Paper restoration warm-up work now runs through a background queue.
- **Cleaner exit**: Reduced lingering menu visuals when closing the app.
- **Note image cache**: Keep one decoded copy of each image, with a cache of approximately 20 images or 50 MB, reducing memory use.
- **Sharper images**: Decode images according to the display DPI and their actual displayed width.
- **Adaptive title bars for narrow papers**: Papers support a smaller minimum width and automatically collapse title-bar buttons.
- Improved link indicators and title-bar alignment: unlinked notes show `⌖`, while notes linked from a todo show `⦿`.
- Updated AvalonEdit to 6.3.1.120.

**Bug fixes and edge cases**

- **Better paper context menus**: Added quick access to Settings and opening notes in an external Markdown editor, highlighted deletion with a warning color, and kept expand/collapse actions in sync with the paper's current state.
- **Preserve hidden papers and capsules after restart**: Fixed startup ignoring their saved `isVisible` state.
- Papers no longer respond to the Windows minimize command: `Win+↓` does not minimize a normal paper window. Maximizing and restoring a maximized window keep their standard Windows behavior.
- Fixed hiding a paper making other edge capsules flash and repeatedly shift upward. Capsules now move directly to their final positions.
- Fixed papers or capsules flickering when switching from a fullscreen window to a normal window of the same app. Fullscreen avoidance is promptly released when switching to the desktop, taskbar, or another window, while fullscreen browser video and presentations remain recognized.
- Fixed choosing an image from a note's context menu sometimes returning the editor to reading mode too early, leaving the image uninserted without an error message.

---

### v3.1

**Appearance and interaction**

- Double-click todo text to select the entire item for quick copying or replacement.
- Right-click the master capsule to open the same menu as the system tray icon.
- Microsoft YaHei and DengXian font presets now prioritize the selected font for digits and English text as well. Segoe UI is used only for missing glyphs, bringing the result closer to custom-font behavior.
- With the system-default font setting, titles, capsules, and other interface text prioritize Microsoft YaHei UI; only note and todo body text retain Segoe priority.

**Images**

- Notes support **WebP** through drag-and-drop, paste, and the image picker. An error is shown if the system does not support it.
- Fixed image insertion failing when dragging from File Explorer or choosing an image from the context menu.
- Copy local image files in File Explorer and paste them directly into a note.
- On startup, unused IDs from removed images are reclaimed for future imports. IDs still referenced by current data or backups are not reused, and existing image IDs remain unchanged.

**Saving data**

- Autosave runs after about 1 second of inactivity, or at least once every approximately 10 seconds during continuous editing.
- If autosave fails and there is no newer save request, it retries after about 10 seconds.
- Crashes now write only `PaperTodo.crash.log`; `data.crash_recovery.json` is no longer generated.
- Data recovery relies on autosave and `data.json` / `data.backup.json`.

**Bug fixes and improvements**

- **Fixed edge capsule dragging while an administrator app is in the foreground**: moving between edges and monitors, and reordering capsules, now work correctly.
- Fixed occasional drag-anchor offsets on mixed-DPI monitors that made a capsule jump as it was grabbed.
- Fixed renaming a paper sometimes moving its expanded window next to the edge capsule.
- Fixed images pasted from some screenshot tools appearing completely transparent because of nonstandard clipboard data.
- Fixed non-image files incorrectly triggering image error messages when dropped or pasted.
- If a .NET runtime component fails to load, the app now exits completely, writes a crash log, and asks the user to reopen it.
- Crash logging continues even if exception classification fails, avoiding a second crash and silent exit when an assembly name is formatted as a path.

---

### v3.0.1

A fix for a bug introduced in v3.0. See the [v3.0 release notes](https://github.com/snownico0722/PaperTodo/releases/tag/v3.0) for the full feature update.

**Bug fixes and edge cases**

- Fixed the newest todo remaining transparent with animations enabled, only becoming visible after another item was added.

---

### v3.0

**A major overhaul**

A broad rebuild and refinement of PaperTodo, with more changes than all previous releases combined.

**Images in notes**

- **Insert images from the clipboard, drag-and-drop, or the menu**. Images fit within the paper's width; remove the corresponding Markdown reference to remove an image.
- New images use the short-ID syntax `![image|100%](i:001)`. Use `![image|widthxheight, 75%](...)` or `{width=75%}` to adjust their display size.
- Images are stored locally in `note-assets.lmdb`. Corruption of a single image affects only that image; deletion and cutting can be undone. Opening a note externally safely exports temporary image files.
- **Automatically compress oversized images** is enabled by default. Images larger than 8 MB or with a longest side over 2048 pixels are compressed; import is canceled if compression fails or the result still exceeds the limits. Images wider or taller than 4096 pixels are rejected. With compression disabled, the original image is retained, subject to an 8 MB limit.

**Shortcuts**

- Record global shortcuts for **Show all, Hide all, Toggle visibility, New todo, New note, and Quit** on the Shortcuts page.
- Added **Quick-open edge capsules**, disabled by default. Left queues use `Ctrl+Shift` + `1–9`; right queues use `Ctrl+Alt` + `1–9`. If the relevant queue is unavailable, lookup falls back to other capsules on the current monitor and then other monitors.
- Added **Open at mouse position** for queue shortcuts, enabled by default. When turned off, papers use their docked or remembered expanded positions.
- **Creating a todo or note with a shortcut** now centers the paper at the pointer, keeping it within the working area, instead of placing it in the upper-left corner.
- Press **Esc** to collapse a paper when capsule mode is enabled.

**Capsules**

- **Rebuilt the capsule system** to improve stability, reduce visual bugs during dragging and multi-monitor use, refine animations and appearance, and make docking to screen edges smoother.
- **Edge and master capsules** dock using their actual widths and expand only inward. Their close area stays against the screen edge; dragging a capsule out of a queue turns it into a complete floating capsule.
- **Fast dragging and cross-monitor movement** keep capsules intact, close to the pointer, and consistently sized across display scales. Transitions also settle correctly after disconnecting a secondary monitor.
- Added **Hide close button on hover**, disabled by default. The edge capsule still extends on hover, but the extended area behaves as part of the capsule.
- A capsule being reordered or moved between queues stays above other capsules.
- Expanding and retracting edge capsules no longer expose gaps or flicker repeatedly as the window moves.
- Fixed capsules remaining extended with their close area open after the pointer left diagonally through an inner rounded corner.
- Edge capsules remain extended while their context menu is open, including when the pointer moves into the menu.
- Limit edge capsule titles to their first N characters; the default “All” preserves existing widths.
- Floating capsules recalculate their width after font changes and at the end of state transitions, keeping the right edge fixed when near the right side of the working area.
- Smoother movement when multiple edge capsules make room, reorder, or collapse toward the master capsule, with fewer stalls in long queues.

**Settings**

- Split Settings into **Basic behavior, Appearance, and Shortcuts**, with less frequently used options in advanced mode.
- Rewrote the ⓘ help descriptions to be shorter and easier to read.

**Appearance**

- Adjust global text size from **80% to 120%**.
- Set **small, medium, or large text sizes and bold styling** separately for note text, todo text, titles, and capsule text.
- Choose Standard, Soft, or Sharp text rendering.
- Added image marker visibility options: Always show, Only while editing, and Always hide. Hiding markers affects only the interface and does not change the original Markdown.
- **Enhanced bold for custom fonts**: when the app directory contains both a `papertodo` body font and a bold font, such as `PaperTodo_Bold.ttf` or `papertodo_bold.ttf`, enabling this option uses the bold file for text marked bold in notes, todos, titles, and capsules.
- Per-note percentage zoom is applied on top of global and body-text size settings.

**System tray**

- Reworked the paper list into a more modern toolbar.
- The gear beside the version number opens Settings.
- The eye icon on the left toggles all papers' visibility.
- Two icons on the right create a todo paper or a note paper.

**Startup and external integration**

Language arguments apply when starting a new app instance. They do not change saved settings or affect an already-running instance. Examples: `--language=zh-CN`, `--language=zh`, and `--lang=en`.

**Key bug fixes and improvements**

- Fullscreen avoidance now affects only the monitor containing the fullscreen app. Topmost papers and capsules on other monitors remain above other windows.
- After todo creation, paste, or completion animations, the original item fades correctly during dragging instead of overlapping the drag preview.
- Scrollbars follow the current color palette. Fixed horizontal scrollbar direction, thumb shape, and track-click behavior.
- Improved **Windows Snap layouts**: paper shadows and margins are hidden while snapped, including Windows 11 layouts with a middle third or a two-thirds main column.
- Fixed restoring paper sizes after Windows Snap.
- With “Hide papers from the window switcher” enabled, **activating a paper no longer brings unrelated, non-topmost papers to the foreground**.
- Papers created through launch arguments, the tray, or first launch appear on **the monitor containing the pointer** and join its edge queue.
- Improved default placement when creating several papers in succession.
- Faster **cold startup and restoration of multiple papers**.
- Improved **continuous typing in long notes**, Markdown line rendering, and autosave overhead. Ordinary edits no longer repeatedly copy the whole document, rescan all image references, or reorganize already-loaded data.
- Edge capsule context menus reliably appear above topmost windows.
- The tray menu now opens correctly on the first right-click.
- Fixed a capsule briefly flashing when opening a collapsed linked note from a todo.
- Fixed excessive resizing clipping paper content.
- A message is shown when note input is truncated at its length limit.
- More reliably terminate script processes started in the current session when quitting, preventing them from blocking exit.

**Other bug fixes and improvements**

- Hiding papers during reordering, disabling capsule mode, or releasing the pointer during a floating transition now ends reordering cleanly and refreshes the queue.
- Queue rearrangements and title-width updates received during dragging are applied together after the drag ends.
- Title changes during expansion or retraction are smoother, without lost updates or abrupt close-area changes.
- Fixed capsule visibility not updating promptly after relinking a note, deleting an empty todo, or undoing/redoing an action.
- Pasting multiple todo lines is treated as one operation.
- Fixed launch arguments sometimes being silently lost while startup restoration was taking a long time.
- Fixed a possible crash when a quit request arrived during startup while the master capsule was still being positioned.
- Fixed rare cases where a failed save was reported as successful.
- Data files with a missing or empty core paper list are no longer treated as a fresh installation. The app tries the backup, and stops startup if neither file can be read, preventing default papers from overwriting existing data.
- Paper titles can contain up to 20 characters. The title display-length setting can be adjusted from 2 to 20.
- Hiding one or all papers preserves their expanded or capsule states. Disabling only edge mode leaves floating capsules at their queue positions instead of moving them to an old position or another monitor.
- Disabling capsule mode correctly expands collapsed papers.
- Fixed dragging of floating capsules, edge capsules, and linked notes triggering too early at high DPI scales such as 150% and 200%, which could turn a small pointer movement into a missed click.

---

### v1.0.0

- **Initial Official Release**: Lightweight, multi-window, zero-framework Windows desktop paper note app built on native .NET and WPF.
