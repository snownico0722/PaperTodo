<h1 align="center">PaperTodo · A Sheet of Paper</h1>

<p align="center">
  <strong>Lightweight desktop notes and todos, ready when you are.</strong><br>
  Capture a thought, check off a task, and get back to what you were doing.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v3.31-3b82f6" alt="version">
  <img src="https://img.shields.io/badge/platform-Windows%20x64-555" alt="platform">
  <img src="https://img.shields.io/badge/.NET-10-512bd4" alt=".NET">
  <img src="https://img.shields.io/badge/UI-WPF-0078d4" alt="UI">
</p>

<p align="center">
  <strong>Language: English | <a href="README.zh-CN.md">简体中文</a></strong><br>
  <a href="https://snownico0722.github.io/PaperTodo/">Official Website</a><br>
  <a href="doc/USER_GUIDE.en.md">User Manual</a> · <a href="doc/CHANGELOG.en.md">Changelog</a><br>
  <a href="https://qm.qq.com/q/Mp7spYLrig">QQ Group: 551612664 — Strange Magic Research Base</a>
</p>

<p align="center">
  The latest stable release is <strong>v3.31</strong>; this document follows the <code>main</code> branch (currently <strong>4.0.0-preview</strong>).
</p>

---

## Preview

| Papers | Markdown Preview |
| :---: | :---: |
| <img src="assets/Home.jpg" alt="Desktop papers" width="100%"> | <img src="assets/Md.jpg" alt="Markdown preview" width="100%"> |

| Capsule Mode | Advanced Capsules |
| :---: | :---: |
| ![Capsule mode](assets/Pill_Mode.gif) | ![Auto-docked capsules](assets/Pill_Plus.gif) |
| Papers can collapse into small capsules to save desktop space. | Collapsed capsules dock to screen edges and slide out on hover. |

---

## Philosophy

- **Paper first** — Each paper is an independent window directly placed on the desktop, with no cumbersome management console to open first.
- **Ready immediately** — Write when you need to, check things off when done; everything saves automatically with minimal friction.
- **No unnecessary management** — No complicated project management models to learn, reducing cognitive load for everyday recording.
- **Native & lightweight** — Built natively with WPF on .NET 10, refusing web wrappers/containers. Fast startup and minimal resource usage.
- **Restrained interaction** — Clean UI with low visual noise. Stays quiet when idle, instantly available when summoned.
> No unnecessary interaction layers. No unnecessary visual focus.

---

## Core Features

### 1. Two Basic Paper Types
- **Todo Paper**: A crisp, efficient task checklist. Supports drag-and-drop reordering, continuous swipe multi-selection, and smart multi-line paste splitting; completed items support auto-clearing or sinking to the bottom.
- **Note Paper**: Lightweight Markdown and visual memo. Supports common Markdown syntax with 3 real-time rendering levels, seamlessly blending editing and reading; supports pasting or dropping local images.

### 2. Edge Capsule & Live Preview Cards
- **Collapse & Dock**: Click the top-right button or press `Ctrl+W` to collapse a paper into a compact pill docked along screen edges, freeing up desktop workspace.
- **Hover Preview Cards (Preview)**: Hover over an edge capsule to smoothly slide out a lightweight, interactive preview card without opening the full paper window:
  - **Todo Preview**: Check or uncheck visible tasks directly within the hover card, or click the background to expand the full paper;
  - **Note Preview**: Instantly renders Markdown formatting and image placeholders;
  - **Intent Prediction & Seamless Handoff**: Built-in mouse motion intent prediction ensures smooth transitions when gliding between adjacent capsules, retracting cleanly when leaving.
- **Multi-Monitor Queues & Master Capsule**: Seamlessly drag capsules to other edges or displays; the master capsule at the top controls the whole queue's expand/collapse and starting height.

### 3. Next-Gen Plugin System (Preview)
- **Desktop Micro-App Expansion**: Papers aren't just for notes—switch any paper into clocks, Pomodoro timers, review journals, and other rich desktop widgets for next-gen Windows desktop workflows.
- **Deep Capsule & Chrome Integration**: Plugins can contribute custom capsule shapes (icons, progress rings/bars, or custom WPF drawings), dedicated edge hover mini cards, and custom top-bar action buttons, with independent safe data storage.
- **Drop-in Simplicity**: Simply place the plugin folder into the `plugins/` directory to load. To develop custom plugins, see the [Plugin Development Manual](plugin-samples/README.md).

### 4. Responsive Native Experience
- **High-Refresh Animations**: Folding, edge previews, and cross-monitor dragging are tuned for high-refresh displays, including 120Hz, 144Hz, and 165Hz screens.
- **Native Windows App**: Built with .NET 10 and WPF, with attention to startup speed and idle resource use.
- **Incremental Note Rendering**: Updates the affected parts of a note as you type, reducing unnecessary work in long documents.
- **Multi-Monitor & Mixed DPI Continuity**: Precisely calibrated for multi-screen layouts with mixed scaling factors, preventing visual distortion or flickering when dragging across monitors.

### 5. Universal Linking & Script Capsules
- **Quick Launch & Universal Linking**: Drag the link handle from another paper's top bar or drop files/folders onto a todo item to bind a quick-launch shortcut.
- **Script Capsules (PowerShell)**: Write `!p` or `!power` on the first line of a note to turn it into a desktop script runner; displays a lightning badge in capsule mode to execute scripts on click.

### 6. Experimental Labs Features (Preview)
- **Local MCP Server**: Supports `--mcp` mode to spin up a standard Model Context Protocol server, enabling external AI assistants (like Claude, Cursor, etc.) to securely read, write, and manage tasks and notes with user authorization.
- **Window Tethering**: Drag the tether handle to attach a paper to any third-party app window, smoothly following the target window as it moves, minimizes, and restores.
- **Scheduled Reminders**: Set custom countdown timers on todo items with tray notifications and alert sounds upon expiration.
- **Idle Behavior & Desktop Integration**: Supports auto-collapsing to capsules on focus loss, compacting the title bar when idle, and global hotkeys to sink papers to the desktop with mouse click-through.

### 7. Customization & Local Data Security
- **Appearance Customization**: Follow system, light, and dark modes with Warm Paper, Ink, Forest, and Rosy color palettes; custom font support (`papertodo.ttf`) and multi-language UI.
- **Local Data Storage**: Notes and todos are stored locally in the app directory (`data.json` and `note-assets.lmdb`), with automatic backups. Core note-taking and task management work offline without an account.

---

## Common Operations & Shortcuts

| Action | Shortcut / Trigger | Description |
| :--- | :--- | :--- |
| **Collapse / Hide Paper** | <kbd>Ctrl</kbd> + <kbd>W</kbd> or Middle-click top bar | Collapses to capsule if enabled, otherwise hides |
| **Cancel / Retract** | <kbd>Esc</kbd> | Cancels multi-selection/drag; collapses paper when idle |
| **Undo / Redo** | <kbd>Ctrl</kbd> + <kbd>Z</kbd> / <kbd>Ctrl</kbd> + <kbd>Y</kbd> | Available in both Todo and Note (up to 100 history steps) |
| **Note Formatting** | <kbd>Ctrl</kbd> + <kbd>B</kbd> / <kbd>I</kbd> / <kbd>K</kbd> | Bold, italic, insert link |
| **Note Font Zoom** | <kbd>Ctrl</kbd> + Scroll Wheel | Adjusts note font scale (click bottom-right percentage to reset) |
| **Retrieve All Papers** | Double-click tray icon | Illuminates and pulls all papers back to visible desktop |

---

## Download & Run

PaperTodo is portable and requires no installation. Download from [Releases](https://github.com/snownico0722/PaperTodo/releases/latest):

- **`PaperTodo-...-self-contained.exe` (Recommended)**: Includes .NET runtime, ready to run out of the box.
- **`PaperTodo-...-no-runtime.exe`**: Smaller download, requires [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) installed on your system.

> **Tip**: Keep the executable in a dedicated writable directory (e.g. `D:\Apps\PaperTodo\`). Avoid running from read-only or temporary zip extraction folders. Place `PaperTodo.ico` in the directory to use a custom tray icon.

On first launch, papers appear on your desktop with a persistent tray icon. If papers are hidden or off-screen, **double-click the tray icon** to pull them all into view.

---

## Documentation & Advanced Guides

- 📖 **[User Manual](doc/USER_GUIDE.en.md)**: Beginner guide, detailed paper & capsule usage, full settings dictionary, and data backup/migration instructions.
- 🧩 **[Plugin Development Manual](plugin-samples/README.md)**: Understand the plugin architecture, API specifications, and sample implementations.
- 📋 **[Changelog](doc/CHANGELOG.en.md)**: User-facing release notes and feature history.

---

## Build & Development

Requires Windows and .NET 10 SDK:

```powershell
git clone --recurse-submodules https://github.com/snownico0722/PaperTodo.git
cd PaperTodo
dotnet build -c Release
```

If cloned without submodules, initialize them first:

```powershell
git submodule update --init --recursive
dotnet build -c Release
```

---

## Feedback & Community

- Issues & Suggestions: [GitHub Issues](https://github.com/snownico0722/PaperTodo/issues)
- QQ Group: [551612664 (Strange Magic Research Base)](https://qm.qq.com/q/Mp7spYLrig)

Special thanks to the [linux.do](https://linux.do/) community.
