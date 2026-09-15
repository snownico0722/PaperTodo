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

- None currently.

### v4.0.0-beta1

- **Paper Count Limit**: Raised the total paper limit from 100 to 200; the in-app cleanup prompt still appears when the limit is reached.
- **Startup and Exit Responsiveness**: Only papers on not-yet-available displays defer restoration; other capsules and papers become available first. Edge notes prepare browsable previews before folded paper shells are built in short, low-priority batches. Context menus and optional initialization no longer block initial restoration, and plugin startup papers await completion instead of polling. Normal exit preserves the final save, withdraws visible surfaces, stops script processes concurrently, and completes the normal window shutdown lifecycle to reduce sequential waits and process-exit delays.

**Edge Preview Cards (Edge Browse)**

- **Real-Time Hover Preview Cards**: Hover over any edge capsule to smoothly slide out a lightweight, interactive preview card without opening the full paper.
  - **Todo Preview**: Displays a simplified task list directly in the hover card. Supports checking/unchecking items to mark complete or undo, and clicking the background to expand the full paper window. Association and quick-launch actions use a more reliable click target and reserve their actual rendered width to avoid unnecessarily wrapping short text. The card shows a top excerpt without scrolling, with an ellipsis at the bottom on overflow.
  - **Note Preview**: Follows the Markdown render setting: Off shows source, Basic now uses the former Enhanced presentation—keeping markers while fading syntax and rendering bullets and rules, with numbers and task markers kept readable—and Full hides syntax markers for compact formatting. Supports headings, emphasis, strikethrough, code blocks, lists, and image placeholders. Text uses the note's typography, rendering settings and per-paper zoom, without additional ordinary-line or blank-line spacing. Card sizing and rendering share an excerpt of at most 16 content blocks and 6000 characters, so omitted content does not reserve empty space. Visible text is prepared in batches; long paragraphs, long code rows and style-dense paragraphs are laid out on a dedicated background thread to reduce UI-thread work; plain text avoids unnecessary temporary allocations. On first open, the body is shown only after the initial prepared result is ready, avoiding partially built content flashing into view. A completed body can be reused when the same live preview briefly retracts and resumes without content or size changes; content, typography, zoom and display changes invalidate it. Inline syntax uses the note’s shared recognizer for nested emphasis, escaping and complex links. The card does not scroll and shows an ellipsis on overflow. After startup restoration and content changes, whole-preview drawing results are prepared ahead of use without retaining hidden cards. With Edge Browse enabled and no more than 10 eligible edge Markdown notes, short notes are preloaded too; larger worksets retain the heavy-content filter. The initial startup batch does not wait for the editing debounce; continuous edits remain coalesced, and leaving the edge queue releases its cache. Preload and live display use the same content dimensions; long-paragraph links require a complete press-and-release click.
  - **Intent Prediction & Seamless Handoff**: Built-in mouse motion intent prediction reduces accidental switches while gliding between adjacent capsules. Preview resizing and neighboring capsule movement run together with less startup drift. Animation updates follow system rendering and stay more continuous as previews expand, retract and switch; a pending update in one queue no longer pauses unrelated queues. By default, when browsing downward, the next card stays near the current cursor whenever it can still fit on screen instead of jumping upward to fill free space; General settings can disable ‘Prefer downward expansion while browsing’ to restore the original upper-space-first placement; previews retract cleanly when leaving the browse corridor. Hiding or restoring edge queues clears outdated hover state. Mouse presses during handoff retain their original position and modifiers; they are forwarded only when that handoff succeeds immediately, while a handoff retry drops the old press instead of replaying it later.
  - **Unified Geometry & Drag Protection**: Preview cards automatically hide during capsule drag-and-drop reordering; empty todo and note papers use a more compact card size.
- **Edge Title Length**: Edge capsule titles can be hidden entirely (0) or shown without a length limit, with step controls cycling through the available values. When hover preview is disabled, truncated titles expand on capsule hover and retract on leave without changing the stored title.
- **Repeat-Click Retrieve / Retract**: Clicking an edge capsule again retracts its expanded paper when that paper is still clearly visible; if the paper is substantially covered by other windows, the same click brings it back to the foreground instead of folding it out of sight.
- **Capsule Sizing & Master Count**: Todo and note edge capsules share a consistent icon slot and title start position, and hiding the close button no longer leaves a dead blank area. The master capsule always shows the exact queue count in both expanded and collapsed states; counts from 1–99 keep a compact fixed two-digit slot, while 100+ expands only as needed so arrow/count changes no longer make the capsule jitter in width.

- **Plugin System & Desktop Micro-Apps**
  - **Codex CLI Bridge**: Send todo items or whole papers to the local Codex CLI, including image attachments, linked paths and related-paper context. Its dedicated paper edits the default prompt: untouched prompts use the built-in text, while edits and intentional clearing are preserved. Includes a PaperTodo plugin creation Skill and development guide, invoked by the default prompt for plugin-building requests.
  - **Plugin Localization**: Plugin manifests can localize names, descriptions, settings, options, and category labels with culture fallback; Native plugins can also read the current PaperTodo UI language. Changing the PaperTodo UI language now offers “Later” or “Restart now”.
  - **Desktop Micro-App Container**: Note papers can be transformed on demand into dedicated desktop micro-apps (such as Pomodoro timers, analog clocks, review pools, or system monitors). Added a dedicated "Plugins" management center in Settings, with data safely isolated and stored under `plugins/data/`.
    - **Web / Native Dual-Mode Runtime Architecture**:
      - **Web Plugins**: Built on Windows WebView2 using standard web technologies (HTML/CSS/JS), ready to run without compilation.
      - **Native Plugins**: Built on .NET 10 + WPF for high-performance, fully custom-rendered desktop interactions.
      - **Three-Tier Runtime Model Separation**: Clearly separates **Body Frontend**, **Edge Mini Hover Frontend**, and **Provider Runtime Daemon Backend**.
    - **Comprehensive Host Capabilities**:
      - **Custom Capsules & Dedicated Mini Views**: Plugins can customize collapsed capsule appearances (supporting icons, text, dynamic progress rings/bars, or pure WPF custom rendering) and provide lightweight mini card views for edge hover.
      - **Deep Todo Integration**: Plugins can contribute right-side action icons and context-menu actions to todo items.
      - **Top Bar & Key Capture**: Plugins can add action buttons and status tags to the paper's top bar, register dedicated global hotkeys, and declare exclusive capture of the <kbd>Esc</kbd> key and body context menu.
      - **Paper Menus & Lightweight Popups**: Plugins can add text entries to paper context menus, read permitted note images, and open lightweight interfaces near a clicked button or menu action that automatically close when focus leaves them.
      - **Managed State & Advanced Settings Panel**: The host manages isolated settings and versioned JSON states (with 10MB/20MB capacity caps and migration support); plugins can declare `advancedSettings` to generate categorized settings pages and `startupPaper` to automatically restore dedicated papers on app launch.
    - **Notes & Samples**:
      - **Security**: No artificial security sandbox is enforced; only install third-party plugins from trusted sources.
      - **Samples**:
        - The `plugin-samples/` directory provides complete source code for native clock, Pomodoro timer, review pool, and Web clock, fully adapted to narrow windows, capsules, and Mini views.
        - Pre-built plugins in the `plugins/` directory can be used directly.

**Todo & Markdown Enhancements**

- **Built-In Full-Text Search**: <kbd>Ctrl</kbd>+<kbd>F</kbd> searches across all built-in todos and Markdown notes, including completed todos, and shows both current-paper and global hit counts. <kbd>Enter</kbd>/<kbd>Shift</kbd>+<kbd>Enter</kbd> or the arrow buttons cycle through matches and jump directly to the matching todo item or note text, automatically showing hidden papers, expanding capsules, and handing the search off between papers. The current todo or note match remains visibly highlighted while the search box keeps keyboard focus. The search box accepts input immediately, grows outward from the paper when needed, can be dragged from its compact right-side rounded bar, count area, or blank area, uses no shadow, and uses vector up/down/close controls.
- **Custom Paper Backgrounds**: Place `papertodo.png`, `papertodo.jpg`, or `papertodo.jpeg` beside `PaperTodo.exe` to share one custom background across todo papers and built-in Markdown notes. Appearance settings can show the original image or blend it with the current paper color. Layout options include Stretch, Center, Bottom Left, Bottom Center, and Bottom Right; non-stretch modes preserve the image aspect ratio and do not crop it. Invalid images safely fall back with a settings warning; oversized images decode with a 4096-pixel longest-edge cap while smaller images keep their original decode size.
- **Full Render Is Now WYSIWYG Block Editing**: Markdown display is simplified to Off / Basic / Full Render. Legacy Basic and Enhanced settings migrate to the new Basic, whose visuals match the former Enhanced mode; Full Render remains Full Render, and new/restored defaults use Basic. With “Full Render” selected, headings, lists, blockquotes, code fences, images, and inline styles are shown directly in their final layout while editing, with most Markdown markers hidden. Heading, emphasis, and link markers collapse out of layout so text reflows compactly; task markers, unordered-list markers, and blockquotes use stable visual slots to reduce horizontal jumps, while ordered-list numbers remain the original source text to avoid flicker when switching edit state. Rendered task checkboxes can be clicked directly to toggle `[ ]` / `[x]`; the source Markdown remains authoritative and each toggle participates in normal undo/redo. The block under the caret reveals its source markers for direct editing, and the whole note returns to read-only rendering on blur. “Markdown rendering animation” adds a short fade and is shown only while Full Render is selected.
- **Unified Markdown Parsing & Consistency**: Headings, blockquotes, lists, code fences, links, basic HTML, escape sequences, and image-code boundaries now share unified Markdown semantics, reducing mismatches across complex nested editing and rendering.
- **Real-Time Markdown Rendering**: Full Markdown visual rendering is also displayed live during editing.
- **Continuous Swipe Multi-Selection**: Click and drag across the left side of todo items to continuously select multiple rows. Supports batch check/uncheck, batch copying, right-click batch deletion, or dragging the whole group to the trash bin.
- **Enhanced Markdown Formatting**: Supports bold-italic syntax (`***text***` / `___text___`), natural combinations of bold, italic, strikethrough, and links, as well as backslash escaping for Markdown punctuation.
- **Incremental Note Rendering**: Standard note editing only refreshes affected local Markdown semantics. Creating, deleting, or editing multi-line code fences lightly tracks the real affected range before refreshing, avoiding unnecessary whole-document work while still keeping long code blocks correct.
- **Continuous Todo Keyboard Editing**: Arrow keys can move across todo items while preserving editing continuity, and key repeat will not race through multiple items.
- **Copy Format Conversion**: Multi-selected todos can be copied with <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> or "Copy as Markdown" to produce Markdown task-list text with completion states preserved. Note selections can use the same shortcut or "Copy as plain text" to strip Markdown formatting. Normal <kbd>Ctrl</kbd>+<kbd>C</kbd> keeps the existing copy behavior.

**Advanced Shortcuts & System Interactions**

- **Settings Sidebar Reorganization**: Settings now use a left navigation sidebar for General, Todo, Note, Appearance, Shortcuts, Plugins, and Labs. Window and capsule options are consolidated into General; General, Todo, and Note can restore their own defaults; each page remembers its scroll position; Advanced mode stays at the bottom of the sidebar; and “Help improve” is simplified and moved into General. Small work areas, high scaling, and cross-DPI monitor moves are re-fitted to the active work area.
- **Advanced Global Hotkeys**: Added hotkeys to lock all papers, toggle paper opacity, toggle capsule opacity, or adjust active paper transparency.
- **Associated Paper Visibility Preservation**: Advanced mode preserves the hidden state of associated target papers when hiding papers via hotkeys, preventing unwanted popup windows during unhide.
- **Auto-Collapse on Focus Loss**: Papers can automatically collapse to capsules when losing focus, with protections against accidental collapse during editing, dragging, context menus, or passive interactions.
- **Instant Paper Dismissal**: Added <kbd>Ctrl</kbd>+<kbd>W</kbd> support for active papers; middle-clicking the top bar executes the same collapse/close action as the top-right button.
- **Post-Expand Focus Reliability**: Restoring papers from capsules more reliably grabs foreground keyboard focus, preventing hotkeys from acting on previously active third-party windows.
- **Smooth Settings UI**: Settings switches and hotkey recording now refresh locally for smoother, flicker-free interaction.

**Experimental Labs Features**

- **Local MCP Server**: Start PaperTodo with `--mcp` to spin up a standard Model Context Protocol server, allowing authorized external AI assistants to read, create, append, and manage notes and todos; Settings can copy the configuration and AI Skill prompt directly.
- **Focus / Resting Transparency**: Papers can fade automatically when unfocused, and normal/docked capsules can fade while resting, with separate opacity controls and options for whether master capsules and active states participate.
- **Window Tethering**: Drag the tether button from the top bar to attach a paper to any third-party desktop window, smoothly following the target window through moving, minimizing, and restoring.
- **Magnetic Edge Snapping**: Floating capsules automatically snap to screen edges or external window boundaries when dragged nearby, sliding out on hover and retracting on leave.
- **Scheduled Todo Reminders**: Set custom countdown timers on todo items (presets, this evening, tomorrow morning, etc.) with tray notifications and alert sounds upon expiration.
- **Inactive Appearance & Topmost Customization**: On focus loss, papers can hide only top-bar buttons or fade the title bar while retaining a complete rounded outline and shadow; the fully hidden area becomes click-through while body position and window size stay unchanged; docked and master capsules can also be configured not to remain system-topmost.
- **Desktop Sinking & Mouse Click-Through**: Hotkeys can send papers or capsules behind normal windows and enable mouse click-through so they behave like part of the desktop background.

**Optimizations & Fixes**

- **Enhanced Data Persistence Reliability**: Hardened primary state saving logic to reduce the chance of file loss under extreme conditions. Backup cadence is reduced and update-time availability is checked first.
- **Input Limit Notices**: Todo batch paste that exceeds count/text limits, and notes that reach the editor protection limit, now show an explicit notice instead of silently dropping or rejecting input.
- **Package Size Optimization**: The Windows single-file package without the .NET runtime was reduced from about 33 MiB to about 17 MiB; testing did not show a meaningful startup-time or working-set regression.
- Fixed “Restore defaults for this page” on the Shortcuts page so it correctly restores the default numpad-key distinction setting.
- Optimized animation fluidity, multi-monitor switching, and window tracking in high-refresh and mixed-DPI environments.
- Fixed an issue where dragging an edge capsule to a secondary monitor could cause it to return to the original monitor when opened.
- Fixed remembered expanded-paper restoration when the paper and its edge capsule are on different monitors; newly remembered positions now restore correctly across different display scaling factors.
- Fixed select dropdown menus in Settings not fully adapting to the active theme palette.

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
- **Move completed items to bottom**: When enabled, checking an item moves it to the end of the completed section; unchecking it moves it to the end of the active section. This behavior is suspended while automatic clearing is enabled.

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
