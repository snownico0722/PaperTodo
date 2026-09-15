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

#### Edge Preview & Capsules

- **Live edge previews**: Hover over a capsule to browse and interact without opening the paper. Todo previews support checking / undoing completion and opening the paper from the card background; separate association and quick-launch targets reserve their actual width to reduce wrapping.
- Note previews follow the Markdown mode: Off shows source; Basic uses the former Enhanced styling with faded markers, bullets and rules while keeping numbers and task states readable; Full hides markers. Headings, emphasis, strikethrough, code blocks, lists and image placeholders follow the note's fonts, sizes, bold settings, text rendering and zoom, without extra ordinary-line or blank-line spacing.
- Previews show the top excerpt without scrolling, with an ellipsis at the bottom on overflow. Notes use at most 16 blocks / 6000 characters for both sizing and display, leaving no space for omitted content. Shared inline parsing handles nested emphasis, escapes and complex links; long-paragraph links require a complete press and release.
- Visible text is prepared in batches; long paragraphs, long code rows and dense styles use a dedicated background thread to reduce UI work, while plain text avoids unnecessary allocations. The first prepared body appears together; the same live preview can reuse its completed body after a brief retraction if content and dimensions are unchanged. Content, typography, zoom or display changes trigger rebuilding.
- Startup restoration and content changes prepare reusable previews without retaining hidden cards. With Edge Browse enabled, all long or heavily styled notes are preloaded; if fewer than 10 qualify, three progressively broader tiers fill the remaining slots. Existing selections win ties, avoiding a sharp drop above 10 notes. Startup skips the editing debounce; continuous edits stay coalesced. Preload and display dimensions match, and leaving the queue releases the cache.
- Adjustable mouse-intent prediction reduces accidental switches. Cards and neighboring capsules expand / retract together with less startup drift; animation follows system rendering for more continuous updates, and queues update independently. Downward browsing keeps the card near the cursor when it fits, avoiding upward jumps to fill space; General settings can disable “Prefer downward expansion while browsing” to restore upper-space-first placement.
- Leaving the browse corridor retracts previews; hiding / restoring queues clears old hover state. Dragging capsules to reorder or leave the queue hides previews; empty todo and note papers use compact cards. Handoff clicks retain their press position and modifiers only for immediate success; retries discard the old click.
- Clicking a capsule again retracts a clearly visible paper or brings a substantially covered paper forward. Titles support hidden (0) or unlimited length with cycling controls; with previews disabled, truncated titles expand on hover and retract on leave without changing the stored title.
- Todo and note edge-capsule icons and titles align, with no empty slot for a hidden close button. Master capsules show exact counts when expanded or collapsed: a fixed two-digit width for 1–99, expanding as needed for 100+, without arrow / count width jitter.

#### Plugin System & Desktop Micro-Apps

- **Desktop micro-apps**: Turn note papers into timers, clocks, review pools or system monitors. Settings adds a Plugins center; isolated data lives in `plugins/data/`.
- **Codex CLI Bridge**: Send todo items or whole papers to local Codex CLI from todo actions or the paper's top bar, including images, linked paths and related-paper context. A dedicated paper edits the default prompt: untouched uses the built-in text, while edits and intentional clearing persist. Includes a plugin-creation Skill and guide, invoked by default for plugin-building requests.
- Web plugins use WebView2 + HTML/CSS/JS without compilation; Native plugins use .NET 10 + WPF for high-performance custom rendering. Body, Edge Mini hover and persistent Provider Runtime are separate components.
- Plugins can customize capsules (icons, text, progress bars / rings or custom drawing) and Mini previews; add right-side todo action icons, todo / paper text menu entries, top-bar buttons / status and global hotkeys; and exclusively handle `Esc` and body context menus. They can read permitted note images and open popups at the clicked button or menu action that close on focus loss.
- The host manages isolated settings and versioned, migratable JSON state with 10 MB / 20 MB limits. `advancedSettings` generates categorized settings; `startupPaper` restores dedicated papers at startup.
- Manifests localize names, descriptions, settings, options and categories with UI-language fallback; Native plugins can read the current language. Language changes offer “Later” or “Restart now”.
- Source examples and ready-to-use plugins live in `plugin-samples/` and `plugins/`: native / Web clocks, Pomodoro and review pools adapted to narrow windows, capsules and Mini views. Plugins have no security sandbox; install plugins only from trusted sources.

#### Todos & Markdown

- **Full-text search**: `Ctrl+F` searches all built-in todos, including completed items, and Markdown notes, showing current-paper / global counts. `Enter` / `Shift+Enter` or arrows cycle to matching items and text, revealing hidden papers, expanding capsules and continuing across papers; matches stay highlighted while the search box keeps focus.
- Search accepts input immediately, opens outward from the paper and can extend beyond its bounds, and moves via its rounded right-side drag bar, count or blank area. It has no shadow and uses vector up/down arrow and close controls.
- **WYSIWYG Markdown editing**: Full Render shows headings, lists, quotes, code fences, images and inline styles while typing, hiding most markers. The caret's block reveals source markers; blur restores read-only rendering for the whole note. Heading, emphasis and link markers collapse for compact layout; task boxes, bullets and quotes keep stable slots, and ordered numbers retain source text to reduce jumps during editing, toggling and continuation.
- Modes become Off / Basic / Full Render. Legacy Basic and Enhanced migrate to Basic with the former Enhanced appearance; Full stays Full, and new / restored defaults use Basic. Rendered task boxes toggle the original Markdown `[ ]` / `[x]` with undo / redo. “Markdown rendering animation” adds an optional short fade and appears in settings only for Full Render.
- **Todo multi-selection and batch actions**: Drag along the left side to select multiple todos, then batch check / uncheck, copy, delete via the context menu or drag the group to the trash. Arrow keys continue editing across items without racing through them on key repeat. The paper limit rises from 100 to 200, retaining the in-app cleanup notice at the limit.
- `Ctrl+Shift+C` copies selected todos as Markdown tasks with completion states, or selected note text as plain text. The context-menu actions are “Copy as Markdown” / “Copy as plain text”; normal `Ctrl+C` is unchanged.
- Markdown supports bold-italic (`***text***` / `___text___`), combinations of bold / italic / strikethrough / links, and backslash escapes. Headings, quotes, lists, fences, links, basic HTML, escapes and image-code boundaries share parsing semantics to reduce nested display, click and editing inconsistencies.

#### Important Fixes & Performance Improvements

- **Saving and input protection**: Improve saving reliability in extreme conditions, reduce backup frequency and check availability before updating. Todo paste exceeding count / text limits and notes reaching the editor protection limit now show explicit notices.
- **Fullscreen avoidance**: Fix papers remaining above some administrator-elevated fullscreen apps in the foreground when fullscreen avoidance is enabled.
- Fix papers opening on the old monitor after moving a capsule, and remembered expanded positions being pulled back to the capsule's monitor; newly saved positions support mixed scaling. Papers opened from capsules obtain focus more reliably, reducing input or `Ctrl+W` going to the previous app.
- **Faster startup and exit**: Only papers on unavailable displays wait. Edge notes prepare previews before folded shells are built in short, low-priority batches; menus and optional setup do not block initial restoration, and plugin startup awaits completion without polling. Exit saves, withdraws surfaces, stops scripts concurrently and completes normal window shutdown with fewer waits.
- Long-note edits update only affected Markdown ranges; multi-line fence changes track the actual range to avoid full reparsing or missed updates. Improve capsule animation, monitor switching and window tracking with high refresh rates and mixed-DPI displays.
- The Windows single-file package without .NET shrinks from about 33 MiB to 17 MiB, with no meaningful startup or working-set regression observed in testing.

#### Appearance, Settings & Interaction

- **Custom paper backgrounds**: Place `papertodo.png`, `papertodo.jpg` or `papertodo.jpeg` beside `PaperTodo.exe` for todos and built-in notes. Appearance settings show the original image or blend it with paper colors, using Stretch / Center / Bottom Left / Bottom Center / Bottom Right; non-stretch modes preserve aspect ratio without cropping. Failed loads fall back safely with a Settings warning; decoding caps the longest edge at 4096 pixels while smaller images retain their original size.
- Settings gains sidebar pages for General, Todo, Note, Appearance, Shortcuts, Plugins and Labs. Window / capsule options move to General; General / Todo / Note restore their own defaults, each page remembers scrolling, Advanced mode stays at the bottom, and “Help improve” moves to General with simpler wording.
- Settings refits to the active work area for small screens, high scaling and cross-DPI moves. Switches and hotkey recording refresh locally to reduce flicker.
- New global hotkeys lock all papers, toggle opacity for all papers or all capsules, or adjust the active paper's transparency. `Ctrl+W` closes / collapses the active expanded paper; middle-clicking its title bar matches the top-right button.
- Auto-collapse to capsules on focus loss respects editing, dragging, menus and passive interactions. Advanced mode preserves associated papers' prior hidden state across shortcut hide / restore, preventing unwanted popups.

#### Experimental Labs Features

- **Local MCP**: Start with `--mcp` for a standard server that lets authorized AI assistants read, create, append and manage notes / todos; Settings can copy the configuration and AI Skill prompt in one click.
- Window tethering: drag the top-bar tether button onto a third-party window to attach a paper and smoothly follow moving, minimizing and restoring. Floating capsules can snap near screen / external-window edges, sliding out on hover and retracting on leave.
- Todo countdown reminders support preset intervals, this evening, tomorrow morning and custom timing, then locate and highlight the item with a tray notification and sound.
- Papers can fade on focus loss, and normal / docked capsules while resting, with separate opacity settings and control over master capsules and active states.
- On blur, hide only top-bar buttons or fade the title bar while retaining rounded outlines and shadows. Fully hidden areas pass clicks through without moving the body or resizing the window. Docked / master capsules can disable forced topmost; hotkeys can send papers / capsules behind normal windows and enable click-through.

#### Other Fixes & Improvements

- Fix “Restore defaults for this page” on Shortcuts failing to reset numpad-key distinction, and Settings dropdowns not fully following the theme.

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
