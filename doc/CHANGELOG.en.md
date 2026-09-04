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
  - **Todo Preview**: Displays a simplified task list directly in the hover card. Supports mouse wheel scrolling, checking/unchecking items to mark complete or undo, and clicking the background to expand the full paper window.
  - **Note Preview**: Lightweight real-time rendering of Markdown formatting, including headings, bold/italics, strikethrough, code blocks, lists, and image placeholders.
  - **Intent Prediction & Seamless Handoff**: Built-in mouse motion intent prediction ensures smooth, continuous transitions when gliding between adjacent capsules, retracting cleanly when leaving the dock corridor.
  - **Unified Geometry & Drag Protection**: Preview cards automatically hide during capsule drag-and-drop reordering; empty todo and note papers display in a clean, ultra-compact card size.

- **Plugin System & Desktop Micro-Apps**
  - **Desktop Micro-App Container**: Note papers can be transformed on demand into dedicated desktop micro-apps (such as Pomodoro timers, analog clocks, review pools, or system monitors). Added a dedicated "Plugins" management center in Settings, with data safely isolated and stored under `plugins/data/`.
    - **Web / Native Dual-Mode Runtime Architecture**:
      - **Web Plugins**: Built on Windows WebView2 using standard web technologies (HTML/CSS/JS), ready to run without compilation.
      - **Native Plugins**: Built on .NET 10 + WPF for high-performance, fully custom-rendered desktop interactions.
      - **Three-Tier Runtime Model Separation**: Clearly separates **Body Frontend**, **Edge Mini Hover Frontend**, and **Provider Runtime Daemon Backend**.
    - **Comprehensive Host Open Capabilities**:
      - **Custom Capsules & Dedicated Mini Views**: Plugins can customize collapsed capsule appearances (supporting icons, text, dynamic progress rings/bars, or pure WPF custom rendering) and provide lightweight mini card views for edge hover.
      - **Deep Todo Integration**: Plugins can contribute right-side action icons and context-menu actions to todo items.
      - **Top Bar & Key Capture**: Plugins can add action buttons and status tags to the paper's top bar, register dedicated global hotkeys, and declare exclusive capture of the <kbd>Esc</kbd> key and context menus.
      - **Managed State & Advanced Settings Panel**: The host manages isolated settings and versioned JSON states (with 10MB/20MB capacity caps and migration support); supports declaring `advancedSettings` to automatically generate categorized settings pages, and `startupPaper` to automatically restore dedicated papers on app launch.
    - **Notes & Samples**:
      - **Security**: No artificial security sandbox is enforced; only install third-party plugins from trusted sources.
      - **Samples**:
        - The `plugin-samples/` directory provides complete source code for native clock, Pomodoro timer, review pool, and Web clock, fully adapted to narrow windows, capsules, and Mini views.
        - Pre-built plugins in the `plugins/` directory can be used directly.

**Todo & Markdown Enhancements**

- **Unified Markdown Parsing & Consistency**: Headings, blockquotes, lists, code fences, links, basic HTML, escape sequences, and image codes now share unified Markdown semantics across both edit and read modes.
- **Real-Time Markdown Rendering**: Full Markdown visual rendering is now also displayed live during editing.
- **Full Render Is Now WYSIWYG Block Editing**: With the “Full Render” mode selected, blocks (headings, lists, blockquotes, code fences, images, inline styles) are shown directly in their final layout while editing, and most Markdown markers are hidden; the block under the caret reveals its markers so you can adjust heading levels, lists, blockquotes, or code directly. The editor stays a single text control, returning to read-only whole-note rendering on blur with caret, selection, undo stack, and copy/paste fully preserved.
- **Continuous Swipe Multi-Selection**: Click and drag across the left side of todo items to continuously select multiple rows. Supports batch check/uncheck, batch copying, right-click batch deletion, or dragging the whole group to the trash bin.
- **Enhanced Markdown Formatting**: Supports bold-italic syntax (`***text***` / `___text___`), natural combinations of bold, italic, strikethrough, and links, as well as backslash escaping for Markdown punctuation.
- **Incremental Note Rendering**: Standard note editing only refreshes affected local Markdown blocks. Multi-line code fence edits track actual ranges before refreshing, eliminating full-document re-parsing and IME typing lag.

**Advanced Shortcuts & System Interactions**

- **Advanced Global Hotkeys**: Added hotkeys to lock all papers, toggle paper opacity, toggle capsule opacity, or adjust active paper transparency.
- **Associated Paper Visibility Preservation**: Advanced mode preserves the hidden state of associated target papers when hiding papers via hotkeys, preventing unwanted popup windows during unhide.
- **Auto-Collapse on Focus Loss**: Papers can automatically collapse to capsules when losing focus, with protections against accidental collapse during editing, dragging, context menus, or passive interactions.
- **Instant Paper Dismissal**: Added <kbd>Ctrl</kbd> + <kbd>W</kbd> support for active papers; middle-clicking the top bar executes the same collapse/close action as the top-right button.
- **Post-Expand Focus Reliability**: Restoring papers from capsules reliably grabs foreground keyboard focus, preventing hotkeys from acting on previously active third-party windows.
- **Smooth Settings UI**: Switches and key recording now use localized element rendering, eliminating flicker.

**Experimental Labs Features**

- **Local MCP Server**: Start PaperTodo with `--mcp` to spin up a standard Model Context Protocol server. Allows external AI assistants (like Claude, Cursor, etc.) to read, create, append, and manage notes and todos with user consent.
- **Window Tethering**: Drag the tether button from the top bar to attach a paper to any third-party desktop window, smoothly following the target window through moving, minimizing, and restoring.
- **Magnetic Edge Snapping**: Floating capsules automatically snap to screen edges or external window boundaries when dragged nearby, sliding out on hover and retracting on leave.
- **Scheduled Todo Reminders**: Set custom countdown timers on todo items (presets, this evening, tomorrow morning, etc.) with tray notifications and alert sounds upon expiration.
- **Idle Morphology & Desktop Sinking**: Auto-hide top-bar buttons or collapse title bars on focus loss; pin papers or capsules to the bottom of the desktop with mouse click-through for true wallpaper integration.

**Optimizations & Fixes**

- **Enhanced Data Persistence Reliability**: Hardened primary state saving logic to prevent file loss under extreme conditions. Optimized backup cadence and added pre-update availability checks.
- Optimized animation fluidity, multi-monitor switching, and window tracking in high-refresh (120Hz/144Hz+) and multi-DPI environments.
- Fixed an issue where dragging an edge capsule to a secondary monitor could cause it to mistakenly snap back to the primary display when clicked.
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
