# PaperTodo User Manual

> **Related Links**: [Back to Home](../README.en.md) · [Plugin Development Manual](../plugin-samples/README.md) · [Changelog](CHANGELOG.en.md)

This document is for everyday users of PaperTodo. If this is your first time using PaperTodo, please start with [1. Quick Start](#1-quick-start); other sections can be referenced as needed.

---

## Table of Contents

- [1. Quick Start](#1-quick-start)
  - [1.1 Installation & Edition Choice](#11-installation--edition-choice)
  - [1.2 First Launch & Philosophy](#12-first-launch--philosophy)
  - [1.3 3-Minute Quick Start](#13-3-minute-quick-start)
- [2. General Paper Operations](#2-general-paper-operations)
  - [2.1 Paper States: Expanded & Capsule](#21-paper-states-expanded--capsule)
  - [2.2 Top Bar Controls & Actions](#22-top-bar-controls--actions)
  - [2.3 Moving, Resizing & Windows Snap](#23-moving-resizing--windows-snap)
  - [2.4 Concept Breakdown: Collapse, Hide, Delete, Exit](#24-concept-breakdown-collapse-hide-delete-exit)
- [3. Todo Paper Complete Guide](#3-todo-paper-complete-guide)
  - [3.1 Adding & Editing Items](#31-adding--editing-items)
  - [3.2 Ordering, Deletion & Batch Actions](#32-ordering-deletion--batch-actions)
  - [3.3 Completed Items Workflow](#33-completed-items-workflow)
  - [3.4 Quick Launch: Linking Papers & Local Files](#34-quick-launch-linking-papers--local-files)
  - [3.5 Scheduled Countdown Reminders](#35-scheduled-countdown-reminders)
- [4. Note Paper Complete Guide](#4-note-paper-complete-guide)
  - [4.1 Edit Mode & Reading Mode](#41-edit-mode--reading-mode)
  - [4.2 Supported Markdown Syntax](#42-supported-markdown-syntax)
  - [4.3 Local Image Insertion & LMDB Storage](#43-local-image-insertion--lmdb-storage)
  - [4.4 Opening in External Editors](#44-opening-in-external-editors)
- [5. Edge Capsules & Live Preview Cards (Edge Preview)](#5-edge-capsules--live-preview-cards-edge-preview)
  - [5.1 Edge Docking & Auto Snapping](#51-edge-docking--auto-snapping)
  - [5.2 Interactive Hover Preview Cards](#52-interactive-hover-preview-cards)
  - [5.3 Multi-Monitor Queues & Reordering](#53-multi-monitor-queues--reordering)
  - [5.4 Master Capsule (Queue Controller)](#54-master-capsule-queue-controller)
- [6. Advanced Playbook: Script Capsules (PowerShell)](#6-advanced-playbook-script-capsules-powershell)
  - [6.1 Script Capsule Declaration Syntax](#61-script-capsule-declaration-syntax)
  - [6.2 Triggering & Persistent Processes](#62-triggering--persistent-processes)
  - [6.3 Security Guidelines](#63-security-guidelines)
- [7. Comprehensive Keyboard Shortcuts](#7-comprehensive-keyboard-shortcuts)
  - [7.1 Built-in Paper Hotkeys](#71-built-in-paper-hotkeys)
  - [7.2 Global System Hotkeys](#72-global-system-hotkeys)
  - [7.3 Edge Capsule Quick Access (1~9)](#73-edge-capsule-quick-access-19)
- [8. Settings Panoramic Walkthrough](#8-settings-panoramic-walkthrough)
  - [8.1 General Behaviors](#81-general-behaviors)
  - [8.2 Visual Styling (Custom Fonts)](#82-visual-styling-custom-fonts)
  - [8.3 Hotkey Configuration](#83-hotkey-configuration)
  - [8.4 Plugin System Guide (Protocol 2.1)](#84-plugin-system-guide-protocol-21)
  - [8.5 Experimental Labs Features (4.0 Advanced)](#85-experimental-labs-features-40-advanced)
- [9. Tray Menu & Command-Line Interface (CLI)](#9-tray-menu--command-line-interface-cli)
  - [9.1 System Tray Menu](#91-system-tray-menu)
  - [9.2 CLI Launch Arguments](#92-cli-launch-arguments)
- [10. Data Backup, Migration & Recovery](#10-data-backup-migration--recovery)
  - [10.1 Directory Structure & Files](#101-directory-structure--files)
  - [10.2 Standard Backup Procedure](#102-standard-backup-procedure)
  - [10.3 Moving to a New PC & Disaster Recovery](#103-moving-to-a-new-pc--disaster-recovery)
- [11. Frequently Asked Questions (FAQ)](#11-frequently-asked-questions-faq)

---

## 1. Quick Start

### 1.1 Installation & Edition Choice

PaperTodo is a green, portable single-executable program that requires no installer. Download the appropriate package from the [Releases page](https://github.com/snownico0722/PaperTodo/releases/latest):

| Edition Identifier | Characteristics | Recommended Audience |
| :--- | :--- | :--- |
| `self-contained.exe` | Bundles .NET runtime (~70–80 MB) | **Recommended for most users**; runs immediately out of the box. |
| `no-runtime.exe` | Tiny file size (~a few MBs) | For systems with **.NET 10 Desktop Runtime (x64)** already installed. |

> [!IMPORTANT]
> Create a dedicated folder on your drive (e.g. `D:\Apps\PaperTodo\`) and run `PaperTodo.exe` from there.  
> **Do not** run from temporary zip extraction directories or read-only folders; otherwise, notes and images cannot be saved to disk.

### 1.2 First Launch & Philosophy

Upon launching `PaperTodo.exe`:
- A default Todo paper appears in the center of your desktop;
- The PaperTodo icon appears in the Windows notification area (system tray).

**PaperTodo has no traditional main management window.** Each paper is an independent desktop interface, while the system tray icon acts as the global control gateway. If papers are hidden or moved off-screen, simply **double-click the tray icon** to illuminate and pull all papers back into view.

### 1.3 3-Minute Quick Start

Follow these 5 steps to master PaperTodo:

1. **Enter a Todo**: Type a task in the blank bottom row of the default Todo paper (e.g., "Team meeting at 3 PM") and press <kbd>Enter</kbd> to add subsequent tasks.
2. **Mark Complete**: Check the box on the left when a task is finished (the text will receive a strike-through).
3. **Reposition Paper**: Click and drag the blank area of the top bar to move the paper anywhere. Click the top-left pin icon to keep it always on top.
4. **Collapse to Capsule**: Click the collapse button on the top right. By default, the paper folds into a small pill and docks cleanly along the screen edge without obstructing your desktop.
5. **Expand & Restore**: Hover your mouse over the docked capsule to view it, or click it to instantly expand it back into a full paper.

Everything you type is saved incrementally in the background—no manual save button needed.

---

## 2. General Paper Operations

<div align="center">
  <img src="../assets/Home.jpg" alt="Desktop paper appearance" width="80%">
</div>

### 2.1 Paper States: Expanded & Capsule

Every paper exists in one of two states:
- **Expanded State**: Standard sticky-note window for reading, typing, checking, and image viewing.
- **Capsule State**: Folded pill-like widget docked along screen edges or floating on your desktop.

Collapse an expanded paper by clicking the top-right collapse button, pressing <kbd>Ctrl</kbd> + <kbd>W</kbd>, or **middle-clicking** the blank area of the top bar.

### 2.2 Top Bar Controls & Actions

The top bar hosts convenient everyday controls:

| Control / Area | Action | Description |
| :--- | :--- | :--- |
| **Pin Icon** | Click | Toggle always-on-top status (automatically yields during full-screen apps if enabled) |
| **Title Text** | Click | Enter title edit mode; press <kbd>Enter</kbd> to commit, <kbd>Esc</kbd> to cancel |
| **Link Icon** | Drag & Drop | Drag onto a todo item to link papers and create a quick-launch shortcut |
| **Window Tether Handle** | Drag & Drop | *(Labs)* Drag onto any third-party app window (browser/IDE) to attach and follow it |
| **New Todo / Note** | Click | Instantly spawn a new Todo or Note paper beside the current one |
| **MD Export Button** | Click | *(Note paper only)* Export to a temporary file and open in your default Markdown editor |
| **Collapse / Hide** | Click | Collapse into a capsule; hides the paper if capsule mode is disabled |

> [!TIP]
> When a paper is resized narrower, secondary action buttons automatically hide to prioritize the title text. Hover over icons to see tooltips.

### 2.3 Moving, Resizing & Windows Snap

- **Moving**: Click and drag any blank space on the top bar.
- **Resizing**:
  - Drag the dot-matrix grip in the bottom-right corner;
  - If set to "Hidden" in "Settings → Visual", you can resize directly from all four borders and corners.
- **Windows Snap**: Drag an expanded paper near screen borders to trigger native Windows Snap layouts; drops window shadows during snapping for clean alignment.

### 2.4 Concept Breakdown: Collapse, Hide, Delete, Exit

| Action | Where it Goes | Data Retention | How to Restore |
| :--- | :--- | :--- | :--- |
| **Collapse** | Folds into an edge capsule or floating pill | Fully Preserved | Click the capsule to expand |
| **Hide** | Leaves the desktop and capsule queue; app stays in tray | Fully Preserved | Right-click tray and check in list, or double-click tray |
| **Delete** | Paper is permanently removed | Deleted | Requires confirmation via tray or right-click menu |
| **Exit** | Flushes data to disk and closes process | Fully Preserved | Double-click `PaperTodo.exe` to re-launch |

---

## 3. Todo Paper Complete Guide

Todo papers are crafted for checklists, daily agendas, and bite-sized action items.

### 3.1 Adding & Editing Items

- **New Item**: Click the bottom blank line to type; press <kbd>Enter</kbd> to commit and automatically insert a new item below.
- **Edit & Double-Click Selection**: Single-click text to edit; **double-click** text to select the entire line for quick replacement.
- **Quick Delete Blank Line**: Press <kbd>Backspace</kbd> on an unmarked empty line to delete it immediately.
- **Smart Multi-Line Paste**: When pasting multi-line text from clipboard, PaperTodo strips bullet points (`-`, `*`), numbers (`1.`, `2.`), and task checkboxes (`- [ ]`), splitting text into discrete tasks.
- **Undo & Redo**: Standard <kbd>Ctrl</kbd> + <kbd>Z</kbd> (undo) and <kbd>Ctrl</kbd> + <kbd>Y</kbd> (redo).

### 3.2 Ordering, Deletion & Batch Actions

- **Drag Reorder & Delete**: Hold the right handle (`≡`) and drag vertically to adjust order; drag down to the bottom trash zone to delete.
- **Continuous Swipe Multi-Selection**: Hold and drag left-click across items from the left margin to continuously select multiple rows.
- **Batch Actions**: Once selected, batch check/uncheck, press <kbd>Ctrl</kbd> + <kbd>C</kbd> to copy, right-click to delete all, or drag the group to the trash.

### 3.3 Completed Items Workflow

Checking an item marks it finished. Customize completion behavior in "Settings → General":
- **Auto-Clear Completed Items**: Clears items from the list immediately upon checking (recoverable via <kbd>Ctrl</kbd> + <kbd>Z</kbd>).
- **Auto-Sink Completed Items**: Automatically moves completed items to the bottom completed section; unchecking returns them to active tasks.

### 3.4 Quick Launch: Linking Papers & Local Files

Each todo entry can serve as a **desktop launchpad**.

#### Link to Another Paper
1. Expand a target paper (Note or Todo).
2. Drag the **dedicated link icon** from the target top bar onto a specific todo item until highlighted, then release.
3. A paper icon appears beside the task; click it to instantly expand and focus the target paper.

#### Link to External Files or Folders
1. Select any file (e.g. spreadsheet, document) or folder in Windows File Explorer.
2. Drag and drop it directly onto the target todo row.
3. A file icon appears; click it to open with the system's default software, or right-click to reveal in explorer or unlink.

### 3.5 Scheduled Countdown Reminders

Right-click any todo item and select "Set Reminder":
- **Quick Presets**: 15 minutes, 30 minutes, 1 hour, this evening, or tomorrow morning;
- **Custom Duration**: Set an exact countdown time;
- **Alert Notification**: Upon expiration, PaperTodo highlights the item, brings it to view, and triggers a system tray bubble notification with an alert chime.

---

## 4. Note Paper Complete Guide

<div align="center">
  <img src="../assets/Md.jpg" alt="Markdown note" width="80%">
</div>

Note papers provide frictionless drafting, scratchpad thinking, and image clipping.

### 4.1 Edit Mode & Reading Mode

- **Edit Mode**: Single-click anywhere inside the body text to enter syntax-highlighted editing.
- **Reading Mode**: Click outside the paper to lose focus; the note smoothly renders formatted Markdown.
- **Link Interaction**:
  - In Reading Mode: Click hyperlinks directly to open in your default browser;
  - In Edit Mode: Hold <kbd>Ctrl</kbd> while clicking links to avoid accidental navigation while placing the cursor.
- **Font Zoom**: Hold <kbd>Ctrl</kbd> + scroll wheel to dynamically zoom text size; a percentage badge displays in the corner (click to reset to 100%).

### 4.2 Supported Markdown Syntax

| Syntax | Example | Rendering Result |
| :--- | :--- | :--- |
| **Headings** | `# Heading 1` / `## Heading 2` | Large bold tiered typography |
| **Bold** | `**Bold text**` | **Bold text** |
| **Italic** | `*Italic text*` | *Italic text* |
| **Bold-Italic** | `***Important***` | ***Important*** |
| **Strikethrough** | `~~Deprecated~~` | ~~Deprecated~~ |
| **Unordered List** | `- First item` or `* First item` | Bullet list |
| **Ordered List** | `1. Step one`, `2. Step two` | Numbered list |
| **Blockquote** | `> Quoted text` | Indented side-bordered quote |
| **Inline Code** | `` `console.log()` `` | Monospaced shaded code tag |
| **Code Block** | <code>\`\`\`<br>code block<br>\`\`\`</code> | Shaded code block |
| **Hyperlinks** | `[Label](https://example.com)` | Clickable blue link |
| **Horizontal Rule** | `---` or `***` | Subtle divider line |

> [!NOTE]
> PaperTodo intentionally omits complex multi-column tables, remote image hosts, and raw HTML blocks to maintain maximum speed. For heavy formatting, click `MD` in the top bar to open in a dedicated editor.

### 4.3 Local Image Insertion & LMDB Storage

Insert images into notes via:
- Pressing <kbd>Ctrl</kbd> + <kbd>V</kbd> after taking a screenshot or copying an image;
- Dragging and dropping image files directly into the note;
- Right-clicking the note text and selecting "Insert Image".

**Storage Architecture**: Images are referenced as `![image](uuid)` in text, while binary assets are safely stored in a transactional, single-file database (`note-assets.lmdb`). Right-click any image to copy or remove it.

### 4.4 Opening in External Editors

Click the `MD` button in the top bar. PaperTodo will package the note and its images into a temporary file and launch your system's default Markdown editor (VS Code, Typora, Notepad, etc.).

> [!WARNING]
> This is a one-way export view. Modifications made in external editors do **not** automatically sync back. Copy modified text back into the paper manually.

---

## 5. Edge Capsules & Live Preview Cards (Edge Preview)

<div align="center">
  <img src="../assets/Pill_Plus.gif" alt="Docked capsule slide-out" width="60%">
</div>

### 5.1 Edge Docking & Auto Snapping

With edge docking enabled, collapsing a paper causes it to dock automatically to screen edges (left or right):
- In idle state, capsules show as slim colored slivers to keep your workspace clear;
- Click any edge capsule to restore it to full size.

### 5.2 Interactive Hover Preview Cards

Hovering over a docked capsule slides out a **real-time interactive preview card** powered by hardware-accelerated animations:

- **Todo Card Interaction**:
  - Displays the active task list;
  - **Check / Uncheck Directly**: Mark items complete or undo directly in the hover card;
  - **Mouse Wheel Scroll**: Scroll through tasks smoothly inside the card;
  - **Click Background**: Click the blank card background to expand the full paper.
- **Note Card Preview**:
  - Live renders Markdown headings, lists, code fences, and image placeholders.
- **Intent Prediction & Seamless Handoff**:
  - Gliding your mouse along adjacent capsules transitions between preview cards without jarring retracts;
  - Cards retract cleanly when moving away from the edge dock corridor.
- **Drag Protection**:
  - Preview cards automatically hide when dragging capsules to reorder or peel away.

### 5.3 Multi-Monitor Queues & Reordering

- **Edge Switching**: Drag a capsule to the opposite screen border or to another monitor to transfer it.
- **Reordering**: Drag capsules vertically along the edge dock to reorder the queue.
- **Multi-Monitor Layouts**: Full support for mixed-DPI multi-monitor setups.

### 5.4 Master Capsule (Queue Controller)

The top-most capsule in the dock is the **Master Capsule**:
- **Batch Toggle**: Click to expand or collapse the entire edge queue;
- **Height Offset**: Drag up and down to adjust the queue's vertical anchor on screen;
- **Global Menu**: Right-click to access the full system tray menu directly.

---

## 6. Advanced Playbook: Script Capsules (PowerShell)

<div align="center">
  <img src="../assets/Power.gif" alt="Script capsule execution" width="60%">
</div>

Turn notes into one-click desktop automation triggers:

### 6.1 Script Capsule Declaration Syntax

Put a script prefix directive on the **first line** of a note, followed by PowerShell code:

```powershell
!p
Get-Service | Where-Object Status -eq 'Running' | Select-Object -First 5
```

Supported prefixes:

| Directive | Execution Behavior |
| :--- | :--- |
| `!p` or `!power` | Auto-selects system PowerShell; displays alerts on errors |
| `!pwsh` or `!ps7` | Enforces modern **PowerShell 7** (pwsh) |
| `!ps5` or `!winps` | Enforces legacy **Windows PowerShell 5.1** |
| `!pf` or `!powerf` | **Persistent Process Mode**: Runs in a shared persistent session, retaining variables |

### 6.2 Triggering & Persistent Processes

- **Execution**: When collapsed into a capsule, a **lightning bolt icon** appears. **Left-clicking the capsule runs the script immediately** instead of expanding the paper!
- **Editing Code**: Right-click the lightning capsule and select "Expand Paper" to edit code.

### 6.3 Security Guidelines

> [!CAUTION]
> Scripts run with your full Windows user privileges. Never paste unverified internet scripts into a note.

---

## 7. Comprehensive Keyboard Shortcuts

### 7.1 Built-in Paper Hotkeys

| Shortcut | Scope | Action |
| :--- | :--- | :--- |
| <kbd>Ctrl</kbd> + <kbd>W</kbd> | Universal | Collapse to capsule (or hide if capsule disabled) |
| <kbd>Esc</kbd> | Universal | Cancel selection/drag; collapses paper when idle |
| <kbd>Ctrl</kbd> + <kbd>Z</kbd> | Todo / Note | Undo previous action |
| <kbd>Ctrl</kbd> + <kbd>Y</kbd> | Todo / Note | Redo previous action |
| <kbd>Ctrl</kbd> + <kbd>B</kbd> | Note | Bold selected text (`**text**`) |
| <kbd>Ctrl</kbd> + <kbd>I</kbd> | Note | Italicize selected text (`*text*`) |
| <kbd>Ctrl</kbd> + <kbd>K</kbd> | Note | Insert Markdown link syntax |
| <kbd>Ctrl</kbd> + Scroll Wheel | Note | Zoom body text size (click percentage to reset to 100%) |

### 7.2 Global System Hotkeys

Configure global system hotkeys in "Settings → Hotkeys":
- Show All / Hide All / Toggle Visibility
- New Todo Paper / New Note Paper
- Exit PaperTodo

### 7.3 Edge Capsule Quick Access (1~9)

Enable "Quick Launch Side Capsules" to summon docked capsules 1 through 9 instantly using key combinations (default Left: <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>1~9</kbd>; Right: <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>1~9</kbd>).

---

## 8. Settings Panoramic Walkthrough

Right-click the tray icon and choose "Settings" to enter the configuration panel. Enable "Advanced Mode" in the bottom-right corner to reveal the "Labs" page.

<div align="center">
  <img src="../assets/Settings.jpg" alt="Settings screenshot" width="80%">
</div>

### 8.1 General Behaviors

- **System Integration**: Run on startup, tooltip toggles, smooth animations.
- **Languages**: Follow System, 简体中文, English, 日本語, and 한국어 (restart required).
- **Top Bar Customization**: Individually toggle New Todo, New Note, or External Open buttons to streamline chrome.
- **Capsule Behaviors**: Capsule mode toggle, edge docking, master capsule, retain placeholder on expand.
- **Todo Logic**: Auto-clear / auto-sink completed items, file drop linking, long title truncation.
- **Full-Screen Yield** (Advanced): Automatically lowers window tier when full-screen games, video players, or presentations are active.
- **Clean Desktop Mode** (Advanced): Hides expanded papers from the Windows taskbar and Alt+Tab switcher.
- **Markdown Rendering**: Four levels (Plain text / Basic / Enhanced / Full Render). With **Full Render**, headings, lists, blockquotes, code fences, images, and inline styles are shown in their final layout directly inside the editor — **while editing too**: most Markdown markers (`#`, `>`, list bullets, code fences) are hidden, and the block under the caret reveals its markers so you can adjust heading levels, lists, or blockquotes directly. Blurring the paper returns to read-only whole-note rendering.

### 8.2 Visual Styling (Custom Fonts)

- **Themes & Palettes**: Follow System, Light, Dark; 4 palettes: **Warm Paper**, **Ink**, **Forest**, and **Rosy**.
- **Resize Grip**: Standard (opaque), Soft (semi-transparent), or Hidden (direct border drag).
- **Typography**: System Default, Microsoft YaHei, DengXian; text rendering modes (Standard / Soft / Crisp).

#### Custom Font Installation
1. Prepare a `.ttf` or `.otf` font file;
2. Rename it to `papertodo.ttf` (or `papertodo.otf`);
3. Place it directly beside `PaperTodo.exe`;
4. If a bold weight exists, rename it `papertodo_bold.ttf`;
5. Restart PaperTodo.

### 8.3 Hotkey Configuration

Interactive visual key recorder with automatic conflict detection.

### 8.4 Plugin System Guide (Protocol 2.1)

PaperTodo 4.0 introduces the **Protocol 2.1 Plugin Architecture**, transforming notes into desktop micro-apps (timers, clocks, review journals, etc.).

#### Plugin Types
- **Web Plugins**: Run via Windows WebView2 using HTML/CSS/JS (no compilation, instant prototyping);
- **Native Plugins**: Built on .NET 10 + WPF for native performance and desktop integration.

#### Protocol 2.1 Extensions
- **Dedicated Capsules & Mini Views**: Plugins customize collapsed capsule visuals and provide lightweight hover mini cards;
- **Top Bar Extensions**: Contribute action buttons and status tags;
- **Todo Actions**: Inject inline/context actions for todo items with state snapshot access;
- **Isolated Storage**: Plugin data is safely sandboxed in `plugins/data/`.

#### Installation Steps
1. Ensure `plugin.json` is located at the plugin's root folder;
2. Right-click the tray icon and select "Exit";
3. Copy the plugin folder into `plugins/<plugin-id>/` (e.g., `plugins/com.example.clock/`);
4. Restart PaperTodo and verify detection in "Settings → Plugins";
5. Right-click any Note paper and select the plugin under "Body Type".

#### Bundled Sample Plugins
The `plugin-samples/` directory includes ready-to-run examples:
- **`com.example.pomodoro`**: Pomodoro timer with countdowns and custom capsule states;
- **`com.example.clock`**: Native WPF desktop analog clock;
- **`com.example.review-pool`**: Task and review journal dashboard;
- **`com.example.web-clock`**: Lightweight Web clock widget.

Copy any sample into `plugins/` to try it immediately!

### 8.5 Experimental Labs Features (4.0 Advanced)

Enable "Advanced Mode" to unlock experimental capabilities:

- **Local MCP Server**: Start with `--mcp` to spin up a Model Context Protocol server for AI assistants (Cursor, Claude, etc.);
- **Window Tethering**: Drag the tether handle onto any third-party app window to dock and follow it smoothly;
- **Countdown Reminders**: Right-click tasks to configure timer notifications;
- **Idle Morphology**: Auto-collapse papers to capsules or auto-compact title bars upon losing focus;
- **Desktop Sinking & Click-Through**: Sink papers to the wallpaper layer with click-through using global hotkeys.

---

## 9. Tray Menu & Command-Line Interface (CLI)

### 9.1 System Tray Menu

Right-click the PaperTodo icon in the notification area:
- Version display
- Show / Hide All Papers
- New Todo / Note Paper
- Paper Roster: Click to locate papers; click `×` to delete
- Settings
- Exit

### 9.2 CLI Launch Arguments

Forward commands to the running instance via shortcuts or third-party launchers:

| Command | Alias | Action |
| :--- | :--- | :--- |
| `PaperTodo.exe --show` | `PaperTodo.exe open` | Wake and center all papers |
| `PaperTodo.exe --hide` | None | Hide all papers (keeps running in tray) |
| `PaperTodo.exe --toggle` | None | Toggle visibility |
| `PaperTodo.exe --new-todo` | `PaperTodo.exe todo` | Create a new Todo paper |
| `PaperTodo.exe --new-note` | `PaperTodo.exe note` | Create a new Note paper |
| `PaperTodo.exe --exit` | `PaperTodo.exe quit` | Save data and exit cleanly |

---

## 10. Data Backup, Migration & Recovery

### 10.1 Directory Structure & Files

```text
PaperTodo/
├── PaperTodo.exe               # Application executable
├── data.json                   # Core data: paper text, positions, and preferences
├── data.backup.json            # Rolling snapshot backup created before every save
├── note-assets.lmdb            # Database for note images
├── plugins/                    # Plugins directory
│   └── data/                   # Isolated plugin configuration and storage
├── papertodo.ttf               # (Optional) Custom font file
└── PaperTodo.ico               # (Optional) Custom tray icon
```

### 10.2 Standard Backup Procedure

1. Right-click the tray icon and choose **Exit**;
2. Copy `data.json`, `data.backup.json`, and `note-assets.lmdb` to a backup location;
3. If using plugins, copy the `plugins/` directory as well.

### 10.3 Moving to a New PC & Disaster Recovery

#### Migration
1. Unpack a fresh `PaperTodo.exe` on your new computer;
2. Copy your backed-up `data.json`, `note-assets.lmdb`, and `plugins/` into the new folder;
3. Launch `PaperTodo.exe`.

#### Disaster Recovery
If an unexpected system shutdown corrupts `data.json`:
1. Make a backup copy of the folder;
2. Rename `data.backup.json` to `data.json`;
3. Launch the app to restore the last automatic snapshot.

---

## 11. Frequently Asked Questions (FAQ)

#### Q1: Why does PaperTodo remain in the system tray when I close a paper?
**A**: Closing a paper collapses or hides it so you can summon it instantly via hotkeys or edge docks. To terminate the program completely, right-click the tray icon and select **Exit**.

#### Q2: What should I do if papers disappear after disconnecting an external monitor?
**A**: Double-click the PaperTodo icon in the system tray. The app automatically pulls all off-screen papers back into the primary monitor's visible area.

#### Q3: I copied my data to a new computer, but note images show broken placeholders.
**A**: The image database was omitted. Images are stored in `note-assets.lmdb`. Ensure you copy `note-assets.lmdb` alongside `data.json`.

#### Q4: Why aren't Markdown tables rendering properly?
**A**: PaperTodo keeps Markdown lightweight (headings, lists, code, quotes) to ensure maximum speed. For complex tables, click the `MD` button in the top bar to open the note in Typora, VS Code, or another full-featured editor.

#### Q5: I changed the interface language in Settings, but some strings did not change.
**A**: Language changes take effect after restarting. Please exit from the tray and reopen PaperTodo.

#### Q6: I added `papertodo.ttf`, but the app's font didn't update.
**A**: Custom fonts load during startup. Please exit completely from the tray and relaunch the app.

---

> For more questions, visit [GitHub Issues](https://github.com/snownico0722/PaperTodo/issues) or join QQ Group **551612664**.
