# PaperTodo Changelog

> **Related Links**: [Back to Home](../README.en.md) · [User Manual](USER_GUIDE.en.md)

This log is written for general and power users alike. It focuses on user-facing features, behavioral changes, enhancements, and fixes.

---

### Planned / To-Do

- None currently.

### Under Evaluation

- **Scoop / Chocolatey Package Managers**: Scoop is a great fit for portable, green installations to simplify install and update flows; Chocolatey is more system-level. Release automation, package manifest maintenance, checksum verification, and user data retention need to be evaluated before adoption.

---

### Unreleased (4.0.0-preview)

**Edge Preview Cards (Edge Browse)**

- **Real-Time Hover Preview Cards**: Hover over any edge capsule to smoothly slide out a lightweight, interactive preview card without opening the full paper.
  - **Todo Preview**: Displays a simplified task list directly in the hover card. Supports checking/unchecking items to mark complete or undo, and clicking the background to expand the full paper window. Association and quick-launch actions use a more reliable click target and reserve their actual rendered width to avoid unnecessarily wrapping short text. The card shows a top excerpt without scrolling, with an ellipsis at the bottom on overflow.
  - **Note Preview**: Follows the Markdown render setting: Off shows source, Basic adds lightweight highlighting, Enhanced fades syntax and renders bullets and rules while keeping numbers and task markers readable, and Full shows compact formatting. Supports headings, emphasis, strikethrough, code blocks, lists, and image placeholders. Text uses the note's typography, rendering settings and per-paper zoom, without additional ordinary-line or blank-line spacing. Card sizing and rendering share an excerpt of at most 16 content blocks and 6000 characters, so omitted content does not reserve empty space. Ordinary source rows use their measured line heights, and long or densely styled paragraphs are prepared one visible line at a time to reduce stalls during expansion; the card does not scroll and shows an ellipsis on overflow.
  - **Intent Prediction & Seamless Handoff**: Built-in mouse motion intent prediction reduces accidental switches while gliding between adjacent capsules. Preview resizing and neighboring capsule movement run together with less startup drift. When browsing downward, the next card stays near the current cursor whenever it can still fit on screen instead of jumping upward to fill free space; previews retract cleanly when leaving the browse corridor.
  - **Unified Geometry & Drag Protection**: Preview cards automatically hide during capsule drag-and-drop reordering; empty todo and note papers use a more compact card size.
- **Edge Title Length**: Edge capsule titles can be hidden entirely (0) or shown without a length limit, with step controls cycling through the available values. When hover preview is disabled, truncated titles expand on capsule hover and retract on leave without changing the stored title.
- **Repeat-Click Retrieve / Retract**: Clicking an edge capsule again retracts its expanded paper when that paper is still clearly visible; if the paper is substantially covered by other windows, the same click brings it back to the foreground instead of folding it out of sight.
- **Capsule Sizing & Master Count**: Todo and note edge capsules share a consistent icon slot and title start position, and hiding the close button no longer leaves a dead blank area. The master capsule always shows the exact queue count in both expanded and collapsed states; counts from 1–99 keep a compact fixed two-digit slot, while 100+ expands only as needed so arrow/count changes no longer make the capsule jitter in width.

- **Plugin System & Desktop Micro-Apps**
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

- **Built-In Full-Text Search**: <kbd>Ctrl</kbd>+<kbd>F</kbd> searches across all built-in todos and Markdown notes, including completed todos, and shows both current-paper and global hit counts. <kbd>Enter</kbd>/<kbd>Shift</kbd>+<kbd>Enter</kbd> or the arrow buttons cycle through matches and jump directly to the matching todo item or note text, automatically showing hidden papers, expanding capsules, and handing the search off between papers. The search box accepts input immediately, grows outward from the paper when needed, can be dragged from its compact right-side rounded bar, count area, or blank area, uses no shadow, and uses vector up/down/close controls.
- **Full Render Is Now WYSIWYG Block Editing**: With “Full Render” selected, headings, lists, blockquotes, code fences, images, and inline styles are shown directly in their final layout while editing, with most Markdown markers hidden. Heading, emphasis, and link markers collapse out of layout so text reflows compactly; task markers, unordered-list markers, and blockquotes use stable visual slots to reduce horizontal jumps, while ordered-list numbers remain the original source text to avoid flicker when switching edit state. Rendered task checkboxes can be clicked directly to toggle `[ ]` / `[x]`; the source Markdown remains authoritative and each toggle participates in normal undo/redo. The block under the caret reveals its source markers for direct editing, and the whole note returns to read-only rendering on blur. “Markdown rendering animation” adds a short fade and is shown only while Full Render is selected.
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
- **Inactive Appearance & Topmost Customization**: On focus loss, papers can hide only top-bar buttons or fade the whole title-bar region so the fully hidden area becomes click-through while body position and window size stay unchanged; docked and master capsules can also be configured not to remain system-topmost.
- **Desktop Sinking & Mouse Click-Through**: Hotkeys can send papers or capsules behind normal windows and enable mouse click-through so they behave like part of the desktop background.

**Optimizations & Fixes**

- **Enhanced Data Persistence Reliability**: Hardened primary state saving logic to reduce the chance of file loss under extreme conditions. Backup cadence is reduced and update-time availability is checked first.
- **Input Limit Notices**: Todo batch paste that exceeds count/text limits, and notes that reach the editor protection limit, now show an explicit notice instead of silently dropping or rejecting input.
- Fixed “Restore defaults for this page” on the Shortcuts page so it correctly restores the default numpad-key distinction setting.
- Optimized animation fluidity, multi-monitor switching, and window tracking in high-refresh and mixed-DPI environments.
- Fixed an issue where dragging an edge capsule to a secondary monitor could cause it to return to the original monitor when opened.
- Fixed remembered expanded-paper restoration when the paper and its edge capsule are on different monitors; newly remembered positions now restore correctly across different display scaling factors.
- Fixed select dropdown menus in Settings not fully adapting to the active theme palette.

---

### v3.31

- **Visual Separation in Advanced Settings**: Advanced options now display with tinted container backgrounds and border grouping for clearer visual hierarchy.
- **Fixed Persistent Refresh for Note Images**: Resolved an issue where notes containing images could repeatedly trigger refresh cycles while idle in the foreground, causing sustained CPU/GPU usage.
- **Note Image Zoom & Resize Optimization**: Reused existing image surfaces during window resizing, eliminating redundant rendering during continuous window dragging.
- **Custom Font Enhanced Bold Support**: Markdown headings, bold spans, and `<b>`/`<strong>` elements correctly adopt custom bold font files (`papertodo_bold.ttf`).
- Fixed an issue where adding items via Enter or multi-line paste could disrupt the "Sink completed items to bottom" sorting order.
- Fixed key recording conflicts with certain third-party IME input methods during global hotkey registration.

---

### v3.3

- **File / Folder Quick Launch**: Drag local files or folders onto todo items to bind quick-launch paths; click to open or right-click to reveal in explorer.
- **Universal Paper Association**: Extended todo item associations to support linking to any paper (Todo or Note) interchangeably.
- **Sink Completed Items to Bottom**: Completed items automatically move to the bottom of the list, returning to the active queue if unchecked.
- **Bare Markdown Link Recognition**: Auto-detects plain `http://` and `https://` URLs in note text as clickable links without breaking code fences.
- **Note Image Memory Optimization**: Decodes large images directly to display target dimensions during import, dramatically lowering peak RAM consumption.
- **Edge Capsule Spacing**: Added configurable spacing between docked capsules (0 / 4 / 8 DIP; default 4 DIP).
- **Multi-Language Selector**: Added official in-app UI language options: Follow System, 简体中文, English, 日本語, and 한국어.
- **Numpad Key Distinction**: Added preference to distinguish between Numpad digits and primary number row keys in global hotkeys.
- **Old Windows Version Compatibility**: Hardened startup lifecycle for edge environments and legacy Windows builds.

---

### v3.2

- **Single-File LMDB Note Image Storage**: Replaced scattered image asset folders with a high-performance, transactional single-file LMDB database (`note-assets.lmdb`).
- **High-Refresh Edge Capsule Synthesizer**: Re-engineered edge capsule dock and slide animations for buttery-smooth 120Hz+ rendering.
- **Multi-Monitor Mixed DPI Recalibration**: Seamless geometry translations when docking and dragging capsules across monitors with different scaling factors.

---

### v3.1 & v3.0

- **Custom Typography**: Added support for placing `papertodo.ttf` and `papertodo_bold.ttf` in the app directory for global typeface customization.
- **Theme Palettes**: Introduced 4 curated color schemes: Warm Paper, Ink, Forest, and Rosy.
- **Script Capsules**: Introduced `!p` / `!power` prefix parsing to turn notes into executable PowerShell script runners.
- **Deep Undo/Redo**: Up to 100 history states recorded for all todo and note modifications.

---

### v1.0.0

- **Initial Official Release**: Lightweight, multi-window, zero-framework Windows desktop paper note app built on native .NET and WPF.
