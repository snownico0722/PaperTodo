# PaperTodo User Manual

**Language: English | [简体中文](USER_GUIDE.md)**

> **Related Links**: [Back to Home](../README.md) · [Plugin Development Manual](../plugin-samples/README.md) · [Changelog](../CHANGELOG.md)
>
> This manual follows **the current main branch**, including features listed under **Unreleased** in the changelog. Released 4.0.0-beta1 builds and older downloads may not include every feature; check the changelog for your installed version. Screenshots illustrate basic operations; follow your version's actual settings layout.

This document is for everyday users of PaperTodo. Start with [1. Quick Start](#1-quick-start), then use the contents to find individual features.

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
  - [2.5 Full-Text Search](#25-full-text-search)
  - [2.6 Exporting Todos and Notes](#26-exporting-todos-and-notes)
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
  - [8.2 Visual Styling (Backgrounds & Fonts)](#82-visual-styling-backgrounds--fonts)
  - [8.3 Hotkey Configuration](#83-hotkey-configuration)
  - [8.4 Plugin System Guide](#84-plugin-system-guide)
  - [8.5 Experimental Labs Features (4.0 Advanced)](#85-experimental-labs-features-40-advanced)
  - [8.6 Workspaces](#86-workspaces)
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

GitHub provides portable Windows x64 builds of PaperTodo on the [Releases page](https://github.com/snownico0722/PaperTodo/releases). The file-placement and migration instructions below describe the portable edition. Check the changelog for your download; features under Unreleased require a later build that includes them.

| Edition Identifier | Characteristics | Recommended Audience |
| :--- | :--- | :--- |
| `self-contained.exe` | Includes the .NET runtime | **Recommended for most users**; no separate runtime installation |
| `no-runtime.exe` | Excludes the .NET runtime; see the download page for its actual size | Systems with **.NET 10 Desktop Runtime (x64)** installed |

> [!IMPORTANT]
> Create a dedicated folder, such as `D:\Apps\PaperTodo\`, and run `PaperTodo.exe` from there.
> **Do not** run from a temporary extraction directory or a read-only folder; data and images might not be saved. Exit from the tray before updating the executable, and keep the existing data files.

### 1.2 First Launch & Philosophy

When you start `PaperTodo.exe`:

- A default Todo paper appears in the center of the desktop;
- The PaperTodo icon appears in the Windows notification area (system tray).

**PaperTodo has no central management window.** Each paper is its own interface, and the tray is the global entry point. **Double-click the tray icon** to bring papers back when they are covered or off-screen.

Each workspace supports up to **200 papers**. Collapsing or hiding a paper preserves it and does not free a slot; delete papers you no longer need.

### 1.3 3-Minute Quick Start

1. **Enter a Todo**: Type in the blank row and press <kbd>Enter</kbd> to continue with another item below.
2. **Mark Complete**: Check the box on the left; the text receives a strike-through.
3. **Reposition Paper**: Drag the blank top-bar area; click the pin to toggle always-on-top.
4. **Collapse to Capsule**: Click the top-right button. With capsule mode and edge docking enabled, the paper folds into the screen edge.
5. **Preview or Open**: With live edge previews enabled, hover to browse content; click the capsule to open the full paper.

Edits save automatically. To find content on another paper, press <kbd>Ctrl</kbd> + <kbd>F</kbd>; see [Full-Text Search](#25-full-text-search).

---

## 2. General Paper Operations

<div align="center">
  <img src="../assets/Home.jpg" alt="Desktop paper appearance" width="80%">
</div>

### 2.1 Paper States: Expanded & Capsule

A paper has two forms:

- **Expanded**: A normal sticky-note window for reading, editing, checking tasks, and viewing images.
- **Capsule**: A small folded pill floating on the desktop or docked along a screen edge.

Click the top-right collapse button, press <kbd>Ctrl</kbd> + <kbd>W</kbd>, or **middle-click the blank top-bar area** to close the active expanded paper. With capsule mode disabled, these actions hide the paper instead of deleting its content.

### 2.2 Top Bar Controls & Actions

| Control / Action | How to Use It |
| :--- | :--- |
| **Pin** | Toggle always-on-top; fullscreen avoidance follows your settings |
| **Title** | Click to edit; Enter commits and Esc cancels. The default limit is six full-width character equivalents; Latin letters, digits, and other half-width characters count as half |
| **`···` Menu** | Open the paper actions menu, also available from the paper's context-menu entry; find unpinned shortcuts, export actions, and Settings here |
| **Drag to Associate** | Drag to a todo to link the current paper; with external-window tethering enabled, you can also drag to another application's window |
| **Switch Workspace** | Available with multiple workspaces enabled; cycle to the next one, or configure selection by name |
| **New Todo / Note** | Create another paper |
| **External Editor** | Temporarily export a built-in Markdown note and open it in another application; see [4.4](#44-opening-in-external-editors) |
| **Collapse / Hide** | Retract the paper; hide it when capsule mode is disabled |

In "Settings → General → Title-bar Shortcuts", drag the five built-in actions to reorder them or move them between **Show in title bar** and **Show in menu**. Todo and Note papers share this layout; actions that do not apply to the current paper are omitted.

New installations pin association and workspace switching; New Note, New Todo, and External Editor start in the menu. Upgrades preserve existing choices. When the title bar is too narrow, pinned built-in shortcuts temporarily move into the menu together. You can hold and drag the association menu item out of the menu too. Plugins may add their own buttons, such as Codex's `>_`.

### 2.3 Moving, Resizing & Windows Snap

- **Move**: Drag an empty area of the top bar.
- **Resize**: Drag the bottom-right grip or any visible paper edge or corner. Standard / Soft / Hidden in "Settings → Visual" changes only the grip appearance, not whether edge resizing works.
- **Windows Snap**: Drag an expanded paper to a screen edge to use Windows Snap. The outer shadow disappears while snapped and returns when unsnapped.

### 2.4 Concept Breakdown: Collapse, Hide, Delete, Exit

| Action | Where It Goes | Data Retention | How to Restore |
| :--- | :--- | :--- | :--- |
| **Collapse** | Edge capsule or floating pill | Preserved | Click the capsule |
| **Hide** | Leaves the desktop and capsule queue; the app stays running | Preserved | Show it from the tray list or double-click the tray |
| **Delete** | Removes the paper | Content deleted | Requires confirmation; do not use as a substitute for hiding |
| **Exit** | Saves data and closes PaperTodo | Preserved on disk | Run `PaperTodo.exe` again |

### 2.5 Full-Text Search

Press <kbd>Ctrl</kbd> + <kbd>F</kbd> in a Todo or Note paper and enter the text to find.

- Search covers todos and built-in Markdown notes in the **current workspace**, with separate **current-paper** and **global** match counts. Switch workspaces to search their content.
- Press <kbd>Enter</kbd> for the next match or <kbd>Shift</kbd> + <kbd>Enter</kbd> for the previous one, or use the search bar's navigation buttons.
- Matches on other papers are located and highlighted automatically. A matching paper opens when it is currently collapsed into a capsule.
- Drag the search bar out of the way when it covers content. Close it when you have finished searching.

### 2.6 Exporting Todos and Notes

Open the `···` / paper context menu on a Todo or built-in Markdown note. Choose the current-paper export action or **Export all papers**, then select a format in the save dialog.

| Export Option | Saved Content |
| :--- | :--- |
| Current paper → Markdown | An `.md` file, with an image folder when the note needs one; keep the folder when sharing |
| Current paper → HTML | An `.html` file with note images embedded, viewable in a browser |
| All papers → Markdown / HTML | A ZIP containing all Todos and built-in Markdown notes in the **current workspace** |

Todo exports preserve order, completion states, and multiline text, while omitting empty editor placeholder rows. Plugin bodies are excluded. Export runs in the background so you can continue editing; check the completion message for missing-image placeholders or other warnings.

Export is for reading, sharing, or using another editor. It is not a full data backup. To migrate workspaces, settings, and plugin data, follow [Chapter 10](#10-data-backup-migration--recovery).

---

## 3. Todo Paper Complete Guide

Todo papers are for everyday checklists, plans, and small tasks.

### 3.1 Adding & Editing Items

- **New Item**: Type in an empty row; Enter inserts another todo below.
- **Line Break in One Item**: Press Shift+Enter. Enter still creates a separate item.
- **Edit and Select**: Click text to edit; double-click to select the item's text.
- **Fold Long Items**: When an item occupies more than four displayed lines, use the arrow above its right drag grip to fold it to two lines or expand it. The ellipsis is only visual; editing reveals the full original text.
- **Find Folded Text**: A match hidden by a fold highlights the item and its expand arrow without automatically expanding it. Click the arrow when you want to read more.
- **Move Between Items**: Use ↑ / ↓ at the editing boundary to continue into an adjacent item. Holding the key does not race through multiple items.
- **Delete a Blank Row**: Press Backspace in an unmarked empty row.
- **Multiline Paste**: Pasted lines are split into separate items, handling bullets, numbering, and Markdown task markers. Shift+Enter support does not change paste into a single-item operation. Check count or length truncation notices for content that was not inserted.
- **Undo and Redo**: Ctrl+Z / Ctrl+Y. One multiline paste is undone as one operation.

### 3.2 Ordering, Deletion & Batch Actions

- **Reorder**: Drag the right handle (`≡`) vertically. Selected items move as a group while preserving their relative order.
- **Scroll While Dragging**: Hover near the top or bottom of the list to auto-scroll, even with a stationary pointer. Scrolling continues while deletion is previewed. Drag away from the trash to cancel the preview; items are deleted only when released over it.
- **Swipe to Multi-Select**: Hold the left mouse button and drag along the left side of items to select consecutive rows.
- **Batch Actions**: Check / uncheck, copy, delete from the context menu, or drag the selected group into the trash.
- **Copy Completion States**: Ctrl+Shift+C copies selected items as Markdown tasks such as `- [ ] Unfinished` and `- [x] Finished`; Ctrl+C performs ordinary copying.
- **Bottom-Bar Toggle**: Hiding the bottom quick-add bar in "Settings → Todo" also disables drag-to-trash deletion. Context-menu deletion remains available.

### 3.3 Completed Items Workflow

Choose how completed items behave in "Settings → Todo":

- **Auto-Clear Completed Items**: Remove an item after checking it; undo to recover an accidental removal.
- **Auto-Sink Completed Items**: Move checked items to the completed area at the bottom; unchecked items return to the unfinished area.

Auto-sink is unavailable while auto-clear is enabled.

### 3.4 Quick Launch: Linking Papers & Local Files

A todo can also be a shortcut to a paper, file, or folder. Related options are in "Settings → Todo".

#### Link to Another Paper

1. Open the paper you want to link.
2. Drag its **Drag to Associate** button onto a todo and release when the target highlights. If the action is in the `···` menu, hold and drag that menu item out instead.
3. Click the paper icon beside the todo to open the linked paper. When linked papers are excluded from the capsule queue, enabling "Click capsule again to retract paper" also lets a second click on this link retract an expanded paper.

#### Link to External Files or Folders

1. Select a file or folder in Windows File Explorer.
2. Drag it onto the target todo row.
3. Click the right-side action to open it with the default application. Right-click the item to open its containing folder or remove the link.

### 3.5 Scheduled Countdown Reminders

Enable **Todo reminders** in "Settings → Todo", then right-click an item to set a reminder.

- **Presets**: Choose a minute / hour interval, this evening, tomorrow morning, or another available preset.
- **Custom Duration**: Enter the countdown duration you need.
- **Due Notification**: PaperTodo locates and highlights the item and sends a tray notification. Enable reminder sound and choose a sound in Settings when you need an audible alert.

Reminders require PaperTodo to remain running with their workspace active; inactive workspaces do not keep reminders running. An installed reminder integration may present a reminder instead of PaperTodo. A valid `papertodo.wav` beside the app takes priority over the selected system sound.

---

## 4. Note Paper Complete Guide

<div align="center">
  <img src="../assets/Md4.0.jpg" alt="Markdown note" width="80%">
</div>

Note papers are for lightweight writing, ideas, and illustrated reminders.

### 4.1 Edit Mode & Reading Mode

Choose a rendering level in "Settings → Note → Markdown":

| Level | Behavior |
| :--- | :--- |
| **Off** | View and edit Markdown mainly as plain source text |
| **Basic** | Keep source markers with basic styling; reading mode fades syntax and displays bullets and dividers |
| **Full Render** | Render headings, lists, quotes, code blocks, images, and inline styles directly, **including while editing** |

- **Edit**: Click the body to place the caret. Full Render does not turn the entire note into plain source; relevant markers appear as you edit them so you can adjust the syntax.
- **Read**: Click outside the paper. The note displays using the selected rendering level.
- **Open Links**: Click directly while reading; hold <kbd>Ctrl</kbd> and click while editing.
- **Zoom Text**: Use <kbd>Ctrl</kbd> + mouse wheel; click the percentage badge to reset to 100%.
- **Copy Plain Text**: Select text and press <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>C</kbd>.

#### Click Markdown Task Checkboxes

Write a task list in the note:

```markdown
- [ ] Organize references
- [x] Send email
```

Select **Full Render**, then click a rendered checkbox to mark the task complete or incomplete without editing `[ ]` / `[x]` manually. This works in both editing and reading modes. If the marker is showing as source, move the caret away so the checkbox reappears. Checking updates the note's content and supports undo.

> [!NOTE]
> Notes still have an input-protection limit. A notice appears when you reach it. Split very long content into multiple papers rather than repeatedly pasting past a warning.

### 4.2 Supported Markdown Syntax

| Syntax | Example | Result |
| :--- | :--- | :--- |
| **Headings** | `# Heading 1` / `## Heading 2` / `### Heading 3` | Heading levels |
| **Bold** | `**Bold text**` | **Bold text** |
| **Italic** | `*Italic text*` | *Italic text* |
| **Bold-Italic** | `***Important***` or `___Important___` | ***Important*** |
| **Strikethrough** | `~~Outdated~~` | ~~Outdated~~ |
| **Unordered List** | `- First item` or `* First item` | Bulleted list |
| **Ordered List** | `1. First step`, `2. Second step` | Numbered list |
| **Task List** | `- [ ] Unfinished` / `- [x] Finished` | Clickable checkboxes in Full Render |
| **Blockquote** | `> Quoted text` | Quoted paragraph |
| **Inline Code** | `` `console.log()` `` | Monospaced code |
| **Code Block** | <code>```<br>code<br>```</code> | Separate code block |
| **Hyperlink** | `[Label](https://example.com)` | Clickable link |
| **Horizontal Rule** | `---` or `***` | Divider |
| **Backslash Escape** | `\*Not italic\*` | Literal asterisks |

Bold, italic, strikethrough, and links can be combined. Version 4.0 uses consistent parsing for headings, quotes, lists, code fences, basic HTML, escapes, and images inside code, improving display and editing of nested content.

> [!NOTE]
> PaperTodo remains lightweight: it does not provide complex tables, remote-hosted images, embedded attachments, or full block-level HTML layout. Use an external editor for more complex formatting.

### 4.3 Local Image Insertion & LMDB Storage

- Press <kbd>Ctrl</kbd> + <kbd>V</kbd> after copying an image or taking a screenshot.
- Drag one or more image files from File Explorer.
- Right-click the note body and choose "Insert Image".

Images appear as Markdown references in the text; their binary data lives in the current workspace's image database: `note-assets.lmdb` for the main workspace and `note-assets2.lmdb` through `note-assets5.lmdb` for secondary workspaces. Right-click a rendered image to copy it or delete its reference. Migrate the text data and image database for the same workspace together.

### 4.4 Opening in External Editors

Choose **External Editor** from the paper's `···` menu, or pin the action through Title-bar Shortcuts. It temporarily exports the current built-in Markdown note and its referenced images, then opens the file with its associated application. Change the file extension in "Settings → Note"; if there is no associated application, PaperTodo uses Notepad.

> [!NOTE]
> External opening is a one-way export. Changes saved in the external editor **do not automatically return to PaperTodo**; copy them back manually. To keep or share a file, use [Exporting Todos and Notes](#26-exporting-todos-and-notes).

---

## 5. Edge Capsules & Live Preview Cards (Edge Preview)

<div align="center">
  <img src="../assets/Pill_Plus.gif" alt="Docked capsule slide-out" width="60%">
</div>

### 5.1 Edge Docking & Auto Snapping

Enable capsule mode and edge docking in "Settings → General" to collapse papers to the left or right screen edge. Click an edge capsule to open its full paper.

- **Repeated Click**: With the corresponding option enabled and an edge slot retained for the expanded paper, another click retracts a clearly visible paper. A substantially covered paper is brought to the front instead.
- **Title Length**: Advanced settings can limit edge-capsule title length, **hide it entirely**, or leave it **unlimited**.
- **Sliding Title Reveal**: With live edge previews disabled, hover over a shortened title to slide it out to its full length; moving away retracts it. This reveals the title, not just the trailing close-button area.
- **Close Button**: Hiding the close button also removes its empty space.

### 5.2 Interactive Hover Preview Cards

With live edge previews enabled, hover over a docked capsule to browse a card without opening the full paper.

- **Todo Preview**: Browse a simplified scrollable list, check / uncheck tasks, and use dedicated linked-paper or file action targets. Click the card background to open the full paper.
- **Note Preview**: Follows the note's Markdown rendering and previews up to **6000 characters**. Open the full paper for longer content; the preview limit does not truncate the original note.
- **Continuous Browsing**: Move between adjacent capsules to switch cards. Adjustable pointer-intent prediction reduces accidental switches. Leaving the browse area retracts the card.
- **Downward Expansion**: "Settings → General → Prefer downward expansion while browsing" is on by default. When there is enough space below, the card stays near the pointer instead of jumping upward to fill a gap. Turn it off to prioritize the upper space released by the previous card.
- **Dragging**: Previews retract when a capsule drag begins, leaving reordering and edge-switching gestures unobstructed.

### 5.3 Multi-Monitor Queues & Reordering

- **Switch Edges**: Drag a capsule to the opposite screen edge or another monitor's edge.
- **Reorder**: Drag capsules up or down within a queue.
- **Remember Expanded Position**: Enable this option to remember a full paper's position on a different monitor from its capsule. Different monitor scaling levels are supported.

### 5.4 Master Capsule (Queue Controller)

The master capsule at the top controls the queue:

- **Batch Toggle**: Click to collapse or expand that side's capsule entries without deleting papers.
- **Height**: Drag vertically to adjust the queue's starting position.
- **Move the Whole Queue**: Pull it out horizontally to retract the queue temporarily. Drop on either edge of the target monitor to move the queue while preserving order. Hidden papers and expanded papers without a queue slot are unaffected.
- **Global Menu**: Right-click to open the global menu.
- **Compact Appearance**: General's advanced options can hide the count and show only an arrow.

"Settings → General" also lets you disable forced always-on-top for docked and master capsules.

---

## 6. Advanced Playbook: Script Capsules (PowerShell)

<div align="center">
  <img src="../assets/Power.gif" alt="Script capsule execution" width="60%">
</div>

A Note paper can be a convenient PowerShell script launcher.

### 6.1 Script Capsule Declaration Syntax

Put a directive on the note's **first line**, followed by the script:

```powershell
!p
Get-Service | Where-Object Status -eq 'Running' | Select-Object -First 5
```

| Directive | Behavior |
| :--- | :--- |
| `!p` or `!power` | Automatically choose PowerShell and report execution errors |
| `!pwsh` or `!ps7` | Use an installed PowerShell 7 |
| `!ps5` or `!winps` | Use Windows PowerShell 5.1 |
| `!pf` or `!powerf` | Reuse a session and retain variables when persistent processes are enabled |

### 6.2 Triggering & Persistent Processes

- Collapsing the note displays a **lightning icon**. Left-clicking runs the script instead of opening the paper.
- To edit the script, right-click the capsule and choose "Expand Paper".
- Persistent processes, PowerShell 7 preference, and hidden execution windows are configured in "Settings → Note" with Advanced Mode enabled.

### 6.3 Security Guidelines

> [!CAUTION]
> Scripts run with the permissions of the PaperTodo process. Do not run unknown or unchecked scripts.

---

## 7. Comprehensive Keyboard Shortcuts

### 7.1 Built-in Paper Hotkeys

| Shortcut | Scope | Action |
| :--- | :--- | :--- |
| <kbd>Ctrl</kbd> + <kbd>F</kbd> | Todo / Note | Search todos and built-in notes in the current workspace |
| <kbd>Enter</kbd> / <kbd>Shift</kbd> + <kbd>Enter</kbd> | Todo editing | New item / line break within an item |
| <kbd>Enter</kbd> / <kbd>Shift</kbd> + <kbd>Enter</kbd> | Search bar | Next / previous match |
| <kbd>Ctrl</kbd> + <kbd>W</kbd> | General | Close the active paper; hide it when capsule mode is off |
| <kbd>Esc</kbd> | General | Cancel an active edit, selection, or drag; close the paper when idle |
| <kbd>Ctrl</kbd> + <kbd>Z</kbd> / <kbd>Y</kbd> | Todo / Note | Undo / redo |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>C</kbd> | Selected todos | Copy Markdown tasks with completion states |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>C</kbd> | Selected note text | Copy plain text |
| <kbd>↑</kbd> / <kbd>↓</kbd> | Todo editing | Move to the adjacent item at an editing boundary |
| <kbd>Ctrl</kbd> + <kbd>B</kbd> / <kbd>I</kbd> / <kbd>K</kbd> | Note | Bold / italic / insert link |
| <kbd>Ctrl</kbd> + mouse wheel | Note | Zoom text; click the percentage to reset |

Plugins may provide their own hotkeys or handle <kbd>Esc</kbd> within their body. Follow the plugin's instructions.

### 7.2 Global System Hotkeys

Record combinations in "Settings → Hotkeys" to use these actions while another application has focus:

- Show All / Hide All / Toggle Visibility, New Todo / Note, and Exit.
- Lock all papers.
- Toggle opacity for all papers or the active paper, or for all capsules.
- Send papers or capsules behind other windows.

Some actions correspond to Labs features that must first be enabled. Combine modifiers such as <kbd>Ctrl</kbd>, <kbd>Alt</kbd>, <kbd>Shift</kbd>, or <kbd>Win</kbd> with a regular key. Choose another combination if the system reports it is already in use.

### 7.3 Edge Capsule Quick Access (1~9)

Enable side-capsule quick access to open papers by their queue numbers. Defaults are <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>1~9</kbd> on the left and <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>1~9</kbd> on the right. The setting to distinguish numpad digits applies to ordinary global shortcuts; it does not change edge-capsule quick-launch sequences.

---

## 8. Settings Panoramic Walkthrough

Right-click the tray and choose "Settings". Version 4.0 uses **left-side navigation** for General, Workspaces, Todo, Note, Visual, Hotkeys, and Plugins. Advanced Mode reveals additional options and the Labs page.

The window adapts to the active work area, display scaling, and monitor changes. Toggles and hotkey recording refresh locally where possible. Use the explanation icons for details.

<div align="center">
  <img src="../assets/Settings.jpg" alt="Settings illustration; 4.0 uses left-side navigation" width="80%">
</div>

### 8.1 General Behaviors

- **General**: Startup, tooltips, animation, language, title-bar shortcuts, and capsule behavior. Fast Animations shortens transitions, is off by default, and does not change reminder timing. Advanced Mode adds taskbar / Alt+Tab visibility and fullscreen avoidance. Restart after changing the language.
- **Todo**: Bottom quick-add bar, auto-clear / auto-sink completed items, paper and file links, reminders, and reminder sounds.
- **Note**: Three Markdown rendering levels, Full Render editing animation, and external-opening extension. Advanced Mode adds large-image compression and script-capsule settings.

See [4.1](#41-edit-mode--reading-mode) for rendering behavior. Basic retains source markers; choose Full Render to keep the final layout while editing and click task checkboxes directly.

### 8.2 Visual Styling (Backgrounds & Fonts)

- **Themes and Palettes**: Follow System, Light, Dark; Warm Paper, Ink, Forest, and Rosy palettes.
- **Skins**: Default paper, Mica, Acrylic, Clear Acrylic, tracing paper, Aero, and pixel style, with independent color customization.
- **Native Materials**: Require Windows 11 22H2 or later; exit and reopen when prompted on first use. Unsupported systems or disabled system transparency may use solid backgrounds.
- **Material Transparency**: Five levels adjust the material on expanded papers and Settings without fading text. Capsules and menus retain lightweight backgrounds that do not transmit the desktop.
- **Resize Grip**: Standard / Soft / Hidden changes appearance only; all three allow resizing from visible paper edges.
- **Typography**: System Default, Microsoft YaHei, DengXian; Standard / Soft / Crisp text rendering.

#### Custom Paper Backgrounds

1. Name an image `papertodo.png`, `papertodo.jpg`, or `papertodo.jpeg` and place it **beside `PaperTodo.exe`**, not in `assets/` or `plugins/`.
2. Restart and open "Settings → Visual → Paper background". This section appears after an image is detected.
3. **Blend with paper colors**: Off shows the original image; on blends it with the current paper colors. This is **not an image visibility switch**.
4. **Stretch** is a separate toggle. On fills the paper proportionally and may crop image edges; off keeps small images at their original size and scales large images down only enough to fit completely.
5. **Position** selects Center, Bottom Left, Bottom Center, or Bottom Right for alignment.

The same image is used for **notes and todos**. After replacing it, toggle blending or stretching, change position, or restart to refresh. To remove the background, move or rename the candidate images and restart. When several candidates exist, PNG is preferred, then JPG, then JPEG.

Decoding caps the longest edge at **4096 pixels** without modifying the original file. Blending, stretching, and position preferences are stored separately in `%LOCALAPPDATA%\PaperTodo\paper-background.json`; see [Backup Procedure](#102-standard-backup-procedure).

#### Custom Font Installation

1. Prepare a `.ttf` or `.otf` font.
2. Rename it to `papertodo.ttf` or `papertodo.otf` and place it beside the executable.
3. Optionally add a matching bold font as `papertodo_bold.ttf`.
4. Exit from the tray and restart to load the font.

### 8.3 Hotkey Configuration

Click an action's recording field and press a combination. The interface checks for conflicts. "Restore defaults for this page" also resets the option to distinguish numpad digits. Configure plugin-provided hotkeys according to the plugin's instructions.

### 8.4 Plugin System Guide

Plugins can turn notes into clocks, Pomodoro timers, or review panels, and add todo actions, top-bar buttons, menus, hotkeys, and dedicated capsule / Mini previews. The current host supports **plugin API 2.1–2.2**, including existing 2.1 plugins. A plugin's `apiVersion` in `plugin.json` is its minimum required host API; a plugin that requires 2.2 cannot simply be installed on an older host.

- **Web Plugins**: HTML/CSS/JavaScript running through WebView2.
- **Native Plugins**: Compiled .NET 10 + WPF plugins.
- **Plugin Data**: Configuration is in "Settings → Plugins". Host-managed data is separated by workspace under `plugins/data/` and `plugins/data2/` through `plugins/data5/`. Separate storage does not mean permission isolation. Back up any files a plugin stores elsewhere according to its own instructions.

#### Installation Steps

1. Obtain a complete, runnable plugin folder. The repository's [`plugins/`](../plugins/) contains built plugins; [`plugin-samples/`](../plugin-samples/) contains source and development documentation. **Native source folders cannot be copied as a substitute for compiled plugins.** The main application release does not bundle these plugins.
2. Exit PaperTodo from the tray.
3. Place the complete folder at `plugins/<plugin-id>/` beside the executable. Its folder name must match `id` in `plugin.json`, and the manifest must be directly inside it, not nested one extra level down.
4. Restart and verify detection in "Settings → Plugins".
5. For plugins that provide a body, right-click a Note paper and select the plugin under "Body Type". Some plugins create an entry paper automatically according to their settings.

Restart after updating or removing plugin files too. Replacing a Native DLL does not hot-reload it.

> [!WARNING]
> PaperTodo **does not provide a security sandbox for plugins**. Treat both Native and Web plugins as trusted code and install only from trusted sources. The data directory does not restrict a plugin's access to the system.
>
> Developers can use the [Plugin Development Manual](../plugin-samples/README.md).

#### Sample Plugins and Codex CLI Bridge

Runnable examples include a native clock, Pomodoro timer, review pool, Web clock, and **Codex CLI Bridge**. Select the plugins you need from the built directory; you do not need to install every example.

Codex CLI Bridge requires Codex CLI to be installed and signed in on this computer:

- A todo's `Codex` / `>_` action sends that item and linked papers, file paths, or supported images. The paper's top-bar `>_` sends the current Todo / built-in Markdown note's full content.
- Both actions **open a foreground PowerShell window by default**, which remains open after completion. Enable **Background execution** in plugin settings to run either action silently.
- The prompt-editor paper starts hidden. Open it through "Settings → Plugins → Codex CLI Bridge → More Settings → Edit Prompt". Deleting the last paper for this plugin removes its global buttons; hiding it does not.
- The default model is `gpt-6-astra`, with `xhigh` reasoning. Leaving either field blank preserves the corresponding local Codex setting. The interface and unedited default prompt follow the app language; user-edited prompts are preserved.
- The plugin-development skill, PaperTodo operation skill, and permission to enable MCP are three independent switches, all on by default. The first two determine the instructions attached to tasks.
- **Allow this plugin to enable MCP** permits a task to turn on MCP and its create, write, direct-delete, and sensitive-settings permissions when needed. Turning this off prevents future automatic activation; it **does not revoke permissions already enabled**. Disable those separately in Labs if needed.

The plugin uses your local Codex login, approval, and sandbox configuration; it is not a free AI service bundled with PaperTodo. For model, reasoning, command path, working directory, and prompt options, see the [Codex CLI Bridge notes](../plugin-samples/PaperTodo.Plugin.CodexCliBridge/README.md).

### 8.5 Experimental Labs Features (4.0 Advanced)

Enable Advanced Mode, then select the Labs capabilities you need:

- **Local MCP**: Enable the feature and permissions, copy the client configuration and AI Skill prompt, and use them in an MCP-compatible client. The client starts `PaperTodo.exe --mcp` to operate on the **current workspace**'s papers, todos, notes, and application settings as authorized.
- **External Window Tethering**: Drag the association action from the title bar or menu onto another application's window to follow its movement, minimization, and restoration.
- **Magnetic Edge Capsules**: Floating capsules snap near screen or external-window edges, slide out on hover, and retract when the pointer leaves. This is a different option from the fixed left / right edge queues.
- **Focus-Loss Automation**: Fade a paper, hide its title icons / title bar, or collapse it when focus is lost.
- **Resting Translucency**: Set separate idle opacity for normal and docked capsules.
- **Send Behind and Click-Through**: Use global hotkeys to place papers or capsules below other windows. With click-through enabled, they no longer intercept mouse input.

MCP gives an external client access to your content; grant trusted clients only the access they need. MCP can be used independently. Codex CLI Bridge can simply send content, or connect to MCP through the settings above when it needs to operate PaperTodo.

#### Reloading Externally Edited Data

A plugin or MCP client can explicitly reload or merge the current workspace's data file. `reload_data` replaces the running state with disk content and may overwrite unsaved local edits. `merge_data` requires the original JSON read before external editing so independent changes can be merged. "Settings → General → Data merge conflicts" offers Ask, External first, or Internal first; Ask lets you compare conflicting values before deciding.

This does not watch the data folder. Direct edits to a running app's `data.json` / `dataN.json` do not automatically sync, and normal autosaves may overwrite them. Exit first when restoring a backup and follow [Chapter 10](#10-data-backup-migration--recovery).

### 8.6 Workspaces

Enable workspaces in "Settings → Workspaces". Initially only the main workspace (1) exists; create up to five. Each stores its own papers, layout, images, and plugin data.

- **Manage and Switch**: Select a workspace card, then click Confirm Switch. Use the pencil beside a name to edit it and the checkmark to save. Renaming does not change numbered data filenames.
- **Quick Switching**: The title-bar or paper-menu workspace action cycles to the next workspace by default. Enable "Choose workspaces by name in the top bar" for named selection. The tray also lets you select a workspace number and confirm.
- **Follow Main Settings**: Secondary workspaces follow the main workspace's software settings by default. Other settings pages, including Plugins, are dimmed; change settings in the main workspace or disable following on the Workspaces page to configure them independently. Papers, layout, images, and plugin-private data are not merged.
- **Disable or Delete**: Disabling workspaces preserves their data. Explicitly deleting a secondary workspace deletes its data; the main workspace cannot be deleted. Back up before deletion.
- **Active Scope**: Search, Show All, Export All, and ordinary MCP content operations apply to the current workspace. Inactive workspaces do not keep paper windows or plugin runtime instances running.

Backups must include the files for every workspace you use; see [Directory Structure & Files](#101-directory-structure--files).

---

## 9. Tray Menu & Command-Line Interface (CLI)

### 9.1 System Tray Menu

Right-click the PaperTodo icon in the Windows notification area:

- The top of the menu shows the version.
- Show / hide all papers in the current workspace, or create a Todo / Note paper.
- With workspaces enabled, select a number in the workspace row, then confirm or cancel the switch.
- The paper list shows titles and states. Click to locate a paper; use its `×` action to confirm deletion.
- Settings opens configuration; Exit saves and closes the application.

### 9.2 CLI Launch Arguments

Use these commands in shortcuts, batch files, or launchers. Paper visibility and creation commands apply to the current workspace:

| Command | Alias | Action |
| :--- | :--- | :--- |
| `PaperTodo.exe --show` | `PaperTodo.exe open` | Show and recall all papers |
| `PaperTodo.exe --hide` | None | Hide all papers while keeping the app running |
| `PaperTodo.exe --toggle` | None | Toggle overall visibility |
| `PaperTodo.exe --new-todo` | `PaperTodo.exe todo` | Create a Todo paper |
| `PaperTodo.exe --new-note` | `PaperTodo.exe note` | Create a Note paper |
| `PaperTodo.exe --exit` | `PaperTodo.exe quit` | Save and exit |
| `PaperTodo.exe --mcp` | None | Start the connection process for an MCP client; see Labs |

Normal GUI launches use a single instance. If PaperTodo is already running, subsequent launches forward commands and exit rather than creating another set of windows. `--mcp` is a separate connection mode, not a normal GUI launch command.

---

## 10. Data Backup, Migration & Recovery

### 10.1 Directory Structure & Files

The portable edition keeps core data beside the executable. Each workspace uses its own files; renaming a workspace does not change its number:

| Content | Main Workspace (1) | Secondary Workspace (2 shown; 3–5 follow the same pattern) |
| :--- | :--- | :--- |
| Paper text, positions, and settings | `data.json` | `data2.json` |
| Short-term automatic backup | `data.backup.json` | `data2.backup.json` |
| Note image database | `note-assets.lmdb` | `note-assets2.lmdb` |
| Host-managed plugin data | `plugins/data/` | `plugins/data2/` |

The main `data.json` also holds the workspace list and names, so retain it when migrating secondary workspaces. Plugin programs in `plugins/<plugin-id>/` are shared by all workspaces. Historical core-JSON archives are stored in `backups/`.

Custom backgrounds, fonts, icons, reminder sounds, and similar files also sit beside the executable. Background blending, stretching, and position preferences are stored separately at `%LOCALAPPDATA%\PaperTodo\paper-background.json`.

`data.backup.json` and its numbered equivalents do not update on every save and do not necessarily represent the state immediately before the last edit.

### 10.2 Standard Backup Procedure

1. **Exit from the tray and wait for the process to finish before copying.** If saving fails, resolve that first; an app that remains running has not completed a safe exit.
2. The simplest method is to copy the **entire application directory** to another backup location, preserving all workspaces, image databases, plugins, historical backups, and custom files.
3. For a selective backup, include every used workspace's JSON, short-term backup, and image database, the complete `plugins/` directory, and the main `data.json`. Include `backups/` if you want historical recovery points. **The three unnumbered data files cover only the main workspace.**
4. Save `%LOCALAPPDATA%\PaperTodo\paper-background.json` separately or configure the background again after migration. Follow plugin instructions for data stored outside the application directory. Files linked from todos are not automatically bundled either.

#### Automatic Core-Data History

The app keeps up to four `PaperTodo-core-*.zip` files in `backups/`, targeting ages near the latest saved state (about 10 minutes), 6 hours, 3 days, and 1 month. It first checks about one minute after startup, then every ten minutes independently of saves and workspace switching. Unchanged content is not archived again. With insufficient history, recovery points are more recent; replacing a long-term archive can also temporarily shift the other target ages.

These archives contain raw contents of existing `data.json` and `data2.json` through `data5.json` files. They **exclude images, plugin data, and short-term backup files**, and do not validate whether JSON is damaged. An unreadable workspace receives brief retries and is then skipped for that scan; the next scan tries again. No empty archive is created when all workspaces are unreadable. Old archives are rotated only after a complete new ZIP is created. Check which workspaces an archive actually contains before restoring it.

> [!IMPORTANT]
> Restoring historical JSON alone can leave images missing or show incorrect images if an image ID has been reused. To restore text and images, use the workspace's **JSON and LMDB image database from the same complete backup**. Automatic ZIPs and content exports do not replace a full backup, and local backups cannot protect against the loss of the entire disk.

### 10.3 Moving to a New PC & Disaster Recovery

#### Migration

1. Exit PaperTodo on the old computer and make a complete backup as described above.
2. Copy the entire backup directory to a writable location on the new computer. Use an application version compatible with your data and plugins; do not launch it until restoration is complete.
3. Restore separate background preferences and any external files stored by plugins or linked from todos as needed.
4. Launch and switch through every workspace you use, checking text, images, plugins, and settings. Keep the old computer's backup until everything is verified.

#### Recovering Damaged or Unwanted Changes

1. Exit and copy the entire existing directory as a recovery snapshot, preserving the original files.
2. Identify the workspace to restore. For the main workspace, **copy** a usable `data.backup.json` to `data.json`; for workspace 2, copy `data2.backup.json` to `data2.json`, and likewise for other numbers. Never overwrite the main `data.json` with a secondary workspace's file.
3. Alternatively, extract the matching numbered JSON from a historical ZIP. To recover images or plugin state, use corresponding files from a complete backup from the same time. When restoring the whole workspace set, use the main workspace catalog and numbered files from the same complete backup too.
4. Restart and inspect each workspace. Short-term backups and historical ZIPs may predate recent edits or contain damaged data, so they cannot guarantee recovery of every change.

---

## 11. Frequently Asked Questions (FAQ)

#### Q1: Why does PaperTodo stay in the tray when I close a paper?
**A**: The button collapses or hides the paper; it does not exit the application. Choose Exit from the tray to close PaperTodo completely.

#### Q2: What if a paper disappears after disconnecting an external display?
**A**: Double-click the tray icon to recall papers. Use Remember expanded position when you need to preserve a cross-monitor placement.

#### Q3: Why did my text migrate but not my images?
**A**: Copy the image database for the same workspace from the same backup: `note-assets.lmdb` for the main workspace or `note-assets2.lmdb` through `note-assets5.lmdb` for secondary workspaces. JSON files and automatic historical ZIPs do not contain the images themselves.

#### Q4: Why do Markdown tables still not render?
**A**: Full Render does not mean every Markdown extension is supported. Choose External Editor in the paper menu to view complex tables elsewhere.

#### Q5: Why did some text not change after I selected another language?
**A**: Exit from the tray and restart to apply the language throughout the application.

#### Q6: Why did adding `papertodo.ttf` not change the font?
**A**: Fonts load on startup. Check that the file is beside the executable, then exit completely and restart.

#### Q7: Why can't I click a task checkbox in a note?
**A**: Select Full Render in "Settings → Note" and use `- [ ]` / `- [x]` task syntax. If the marker is showing as source, move the caret away before clicking the rendered box.

#### Q8: Why is the background still visible with blending turned off?
**A**: Off displays the original image. To remove it entirely, move or rename the candidate background files beside the executable and restart.

#### Q9: Why does a copied plugin not appear?
**A**: Check that it is a runnable build, its directory name matches `id` in `plugin.json`, it is not nested an extra level, and its required plugin API falls within the host's supported 2.1–2.2 range. Restart and check "Settings → Plugins". Native source is not a substitute for a compiled plugin.

#### Q10: Why are settings dimmed in a secondary workspace?
**A**: It follows the main workspace's settings. Change them in the main workspace or turn off Follow Main Workspace Settings on the Workspaces page.

#### Q11: Why is the Codex prompt paper missing while its buttons remain?
**A**: The prompt paper is hidden by default. Open it through the plugin's More Settings → Edit Prompt; there is no need to delete and recreate it.

#### Q12: Why is a documented feature missing from my installed build?
**A**: This manual includes unreleased main-branch features. Check your installed version's changelog first. Available shortcuts may also be in the paper's `···` menu, and some options require Advanced Mode.

---

> For more questions, visit [GitHub Issues](https://github.com/snownico0722/PaperTodo/issues) or join QQ Group **551612664**.
