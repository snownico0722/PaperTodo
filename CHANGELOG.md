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

#### Papers, Title Bar & Capsules

- **Paper menus and title-bar shortcuts**: Expanded papers share the same actions through their `···` and context menus, with Quick, the current paper title and Settings available.
  - Under Settings → General → Title-bar Shortcuts, drag and reorder the four built-in actions (Associate by Drag, New Note, New Todo, External Editor) between **Show in title bar** and **Show in menu**. The layout is shared by Todo and Note papers, with no separate title-bar customization menu or visibility checkboxes.
  - New installations pin only Associate by Drag to the title bar; New Note, New Todo and External Editor appear in that menu order. Existing pinned choices are preserved on upgrade. Narrow papers move the built-in shortcuts into the menu as a group without repeating actions already visible in the title bar. Plugin actions have the lowest priority and use only spare space. Drag-to-associate remains available from either location.
  - The settings page uses the capsule-style Todo and Note icons and dedicated link/external-link icons, with improved action-area framing, icon alignment, spacing and help text. Help Improve now sits below Fast Animations.
- **Fast animations**: An optional General setting shortens visual transitions to around 60% of their usual duration without turning animations off. It is disabled by default and does not affect reminder or other non-visual timers.
- **More compact edge previews**: Built-in Todo and Note hover previews have a maximum height of 320 DIP, while shorter content still determines its own height. Plugin Mini size limits are unchanged.
- **Drag the entire master-capsule queue**: Pull the master capsule horizontally to move the whole edge-capsule queue to the other side of a screen or the left/right edge of another monitor, preserving member order. The original queue temporarily retracts while the master becomes a floating capsule that follows the pointer; on release it flies smoothly to the final position instead of jumping there. Hidden papers and expanded papers without a queue slot are unaffected. Vertical dragging still adjusts only the queue's starting height.
- **Compact master capsule**: An Advanced option in General lets the master capsule show only its arrow, hiding the queue count.

#### Appearance, Skins & Materials

- **Seven experimental skins**: Paper, Mica, two Acrylic styles, tracing paper, Aero and pixel style, each with independent colors. Aero offers clear tint, localized reflections and position-aware parallax without desktop capture. Switching skins preserves editors, text undo history and paper resizing: visible paper edges remain draggable, and Standard / Soft / Hidden resize grips change only the grip's appearance.
- **Native Windows materials**: Expanded papers and Settings can use native Mica, Acrylic, tracing-paper and Aero backdrops on Windows 11 22H2 or later, optionally keeping their active appearance. The first activation requires exiting and reopening PaperTodo. On Windows 11 24H2 (build 26100) or later, the material can extend continuously through the title bar; other supported systems use the compatible path. Solid backgrounds remain where native transparency is unavailable or unsuitable, and high-contrast mode hides decorative effects.
- **Five material-transparency levels**: Very Low / Low / Medium / High / Very High adjust the tint and covering strength of native and auxiliary materials rather than reducing opacity for the entire window. Text and controls therefore remain fully legible, while Aero reflections, relief and tracing-paper fibers are not removed by the chosen level.
- **Material backgrounds and transition performance**: Layered capsules and menus using Mica, either Acrylic variant or tracing paper capture a local background snapshot when shown and keep it stable while visible. Capsule dragging shares a blurred desktop snapshot, refreshing the final local background when released. Queue transitions retain the existing material rather than repeatedly capturing the desktop; purely visual transition covers do not intercept clicks on underlying applications. Material updates during paper collapse/expand and capsule layout changes are coordinated to reduce stutter.
- **Custom paper-background scaling**: Stretch now fills the paper while keeping the image aspect ratio. With Stretch disabled, small images stay at native size and large images only scale down to fit completely, rather than being enlarged or unnecessarily cropped.

#### Todos & Notes

- **Todo bottom-bar toggle**: The bottom quick-add area can be hidden; the toggle is enabled by default. Hiding it also disables dragging todos into the trash for deletion.
- **Reorder selected todos as a group**: Drag multiple selected todos together without changing their relative order. Moving near the top or bottom of the list scrolls smoothly, including while the trash preview is shown. Items are deleted only if the pointer is released over the trash.
- **Multiline and collapsible todos**: `Shift+Enter` inserts a line break into the current todo; `Enter` still creates the next one. When an item exceeds four displayed lines, an arrow above its drag grip can collapse it to two lines or expand it again. The collapsed second line ends with an ellipsis, and editing temporarily reveals all text. A search match inside hidden lines highlights the corresponding todo and expansion arrow without automatically expanding it.
- **Linked-paper title updates**: Changes to the paper-title length limit immediately update linked paper names shown in Todo items.
- **Open notes in an external editor**: PaperTodo uses the Windows default application associated with the configured export extension; if no association exists, it falls back to Notepad regardless of the extension.

#### Plugins, MCP & Codex

- **Plugin API 2.2 and compatibility**: Existing API 2.1 plugins remain load-compatible. A plugin's `apiVersion` explicitly specifies the **minimum host API it requires**: a plugin can load when its requirement falls between the host's minimum supported API and its current version, but not when it needs a newer host or an older unsupported API. New 2.2 capabilities include hidden-at-start plugin papers (`hidden`), Settings action buttons (`action`), the public application Settings API/permissions and cross-paper presentation controls.
- **Cross-paper presentation**: MCP and Native/Web plugins can show, hide, expand, collapse or activate existing papers by ID. Show preserves the paper's current collapsed state; Hide does not delete its content. MCP paper queries now also return collapsed state.
- **Application Settings API**: MCP and Native/Web plugins can query and change user-facing preferences through the same typed settings catalog, with rollback if saving fails. MCP can also unlink a Todo from its associated paper.
- **MCP tool improvements**: Invalid argument types, missing required fields and errors inside arrays identify the affected argument or position rather than returning a generic failure. Paper queries expose `paper_id` alongside `id`; create-paper schemas describe the configurable title limit, so normal short titles do not require an earlier settings query. `update_todo` accepts a zero-based `order` for reordering items.
- **Creating todos through plugins**: New or appended todos may include their initial completion state, reminder and linked-paper information without also requesting permission to modify existing todo content.
- **Plugin actions and hints**: Plugins can expose Runtime-handled Settings action buttons such as Refresh Now or Test Connection without registering a shortcut; unavailable actions are disabled. Action-button and top-bar label tooltips support multiple lines, and web/email links in plugin popups open using the system's default applications.
- **Plugin-body reads and writes**: Reading papers, todos, notes or images no longer commits unrelated plugin bodies; external writes do not globally commit unrelated papers. When the target is a built-in Markdown note currently being edited, pending user input on that paper is applied before the external write. Background plugin saves and associated cleanup after deleting a paper no longer block input, and edits made during a save remain pending for a subsequent save.
- **Plugin data and failure handling**: Plugin state uses its normal data files. Unreadable existing data reports an error rather than automatically creating an empty recovery state, and old recovery files are not deleted. Ordinary theme, font or activation-notification failures do not discard the entire plugin body. Initial background startup failures do not trigger automatic retries; a Runtime that has previously started gets limited recovery attempts and stops on repeated failure instead of repeatedly reloading or rapidly retrying failed saves.
- **Codex CLI Bridge**:
  - The default model is GPT-6 Astra (`gpt-6-astra`) with reasoning still at `xhigh`. Leaving the model field blank preserves the user's Codex CLI model choice. Sending a task now opens a foreground PowerShell window by default; background execution remains optional.
  - The plugin UI and default prompts follow PaperTodo's current language without replacing prompts the user has edited. Common startup, execution and model options stay on the plugin card; reasoning and path options are in **More Settings**.
  - Three **independent, default-on** switches control the plugin-development skill, PaperTodo operation skill and permission for the plugin to enable MCP when needed. Skills are injected when a task is sent, separately from the editable default prompt. With MCP permission enabled, the plugin may enable the MCP master switch and its write/delete permissions without altering unrelated PaperTodo feature switches.
  - Built-in prompting keeps todos concise and moves longer details into a linked Note only when todo-to-paper linking is enabled. Notes avoid unnecessary verbosity without discarding useful information.
  - The Codex prompt-editor paper starts hidden and can be expanded directly through **More Settings → Edit Prompt**. On-demand MCP activation also reports whether activation actually succeeded.

#### Fixes, Performance & Other Improvements

- **Todo performance and editing continuity**: Reduced delays with many todos during startup, opening papers and bulk paste. Per-row context menus are prepared when first opened, while mouse right-click, the Menu key and `Shift+F10` work immediately. Reordering and undo/redo update only changed rows, reducing stutter and preserving editors, keyboard focus and selections. Show/Hide All avoids unnecessary linked-Todo editor reconstruction and better preserves text selections and undo history.
- **Edge-preview click targeting**: Fixed rapid transitions between edge previews leaving clickable areas or forwarded click locations at older animation positions.
- **Repeat-click linked-paper behavior**: With **Click capsule again to retract paper** enabled, clicking a Todo link to an expanded paper that does not have its own capsule retracts it; clicking again reopens it. This setting was renamed from **Click edge capsule again to retract paper**.
- **Capsule position memory**: Expanded paper position and size are remembered even with **Keep edge capsule visible while expanded** disabled. Temporarily switching off capsule, edge-capsule or master-capsule features no longer clears the queue's remembered starting height; re-enabling restores it, while explicitly restoring defaults still clears that memory.
- **Multi-monitor startup and focus**: A paper with otherwise valid coordinates briefly waits once if its display has not yet been detected during startup, before ordinary off-screen recovery; other papers restore immediately. Closing, hiding or deleting the active paper hands focus to an appropriate window without pulling an obscured background window forward, and operations on background papers do not steal focus.
- **Windows startup-app status**: Disabling PaperTodo through Windows Startup Apps or Task Manager is now reflected correctly in PaperTodo Settings, and startup can be enabled again from within the app rather than remaining incorrectly marked as on.
- **Todo reminders**: Fixed several due reminders being considered delivered when tray notifications were unavailable even though some were never shown. Reminders not actually surfaced remain eligible for the normal retry path.
- **Web Mini interaction**: Fixed some plugin Mini surfaces displaying normally after restoration but leaving certain controls unclickable.
- **Persistent PowerShell scripts**: Changing Prefer PowerShell 7 or Hide Run Window now affects future runs only, without stopping the current persistent process; explicitly disabling persistent mode still stops it.
- **Review Archive data**: Unreadable, corrupt or unsupported existing archive data now raises an explicit error rather than being treated as an empty archive that could overwrite the original file on the next save.

#### Website & Dependencies

- **Interactive website**: A shared PaperTodo 4.0 workbench demonstrates papers, edge previews, Markdown, scripts and plugin creation. Updated FAQs, paper themes and dark mode are included, and the site scales proportionally above 1600 × 900. First visits follow the browser's Chinese/English language, while an explicit URL language or saved preference takes priority.
- **Dependency updates**: Updated WebView2 to `1.0.4191.47`, Microsoft.Extensions.Hosting to `10.0.12` and the MCP SDK to `2.2.0` for runtime stability and compatibility fixes.

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
