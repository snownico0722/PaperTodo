# PaperTodo Plugin Development

**Language: English | [简体中文](README.zh.md)**

This is the **current PaperTodo plugin development manual**. It documents the current plugin contract, runtime boundaries, build workflow, and examples, with a concise API-version history and compatibility summary. Detailed historical trade-offs remain in `doc/DECISIONS.md`.

New plugins use:

```json
"apiVersion": "2.2"
```

The current host exposes Protocol `2.2` and remains backward-compatible with `2.1`. Protocol `2.0` and earlier are no longer load-compatible. New plugins should target `2.2`; existing `2.1` plugins can keep running as long as they do not declare 2.2-only features.

### Plugin API version history

`apiVersion` is the **minimum PaperTodo plugin API required by the plugin**, not the plugin's own release version. The host loads a plugin only when:

```text
MinimumSupportedApiVersion <= plugin.apiVersion <= CurrentApiVersion
```

A newer host may load an older compatible plugin. An older host must reject a plugin that requires a newer API. PaperTodo currently supports **2.1 through 2.2**.

| API | Status | Main contract milestones |
| --- | --- | --- |
| 1.x | Legacy / unsupported | Early paper-body contracts. Known milestones include 1.2 moving plugin settings/state into the independent plugin data store, and 1.8 adding host-owned Edge Mini views. |
| 2.0 | Experimental / unsupported | Transitional generation that introduced host-rendered Top Bar concepts while still carrying separate provider app Runtime and per-Paper Web Runtime paths. It was superseded rather than kept load-compatible. |
| 2.1 | Supported minimum | Consolidated background work into one provider Runtime; Body/Mini are frontend surfaces. Includes Workspace Paper/Todo/Note access, Runtime state/Papers, host-rendered Top Bar and Todo contributions, global shortcuts/custom shortcut actions, paper menus, note-image reads and temporary popups. |
| 2.2 | Current | Adds cross-paper presentation, public application Settings API (`settings.read/update`), settings `type: "action"` command buttons, and `startupPaper.presentation: "hidden"`. New plugins should target 2.2. |

**Versioning rule:** a backward-compatible new plugin-visible contract (manifest field/type, permission, event, public API surface, or new observable semantics) advances the minor version after the current minor is published. Host-only implementation changes and bug fixes do not. Breaking an existing plugin contract advances the major version. Multiple compatible additions developed before one minor is released may ship together in that minor.

Public plugin types in [`../PaperTodo.Plugin.Abstractions/`](../PaperTodo.Plugin.Abstractions/) are the compile-time contract. Actual host validation and runtime behavior are defined by the current host code. Read [`../doc/ARCHITECTURE.md`](../doc/ARCHITECTURE.md) only when you need to understand PaperTodo's internal ownership model; plugin authors do not need to study the host architecture before getting started.

> **Trust boundary: PaperTodo does not provide a security sandbox for plugins.** Both Native and Web plugins must be treated as trusted code. Install plugins only from sources you trust.

## 1. Quick Start

PaperTodo supports two plugin types:

| Type | Best for | Entry point | Build |
| --- | --- | --- | --- |
| Web | HTML/CSS/JS, local state panels, lightweight interactions | Local `entry` page; optional Runtime background entry (`runtime` in the manifest, otherwise `runtime.html` next to `entry`) | No compilation required |
| Native | .NET/WPF, complex local UI, native dependencies, custom WPF capsule/mini surfaces | DLL implementing `IPaperBodyPlugin`; optional `IPaperPluginRuntimeProvider` for a single provider Runtime | `dotnet publish`; the repository script is recommended |

Both plugin types are ultimately installed under:

```text
plugins/<plugin ID>/
```

The directory name must match the `id` in `plugin.json`.

### 1.1 Minimal Web plugin

Directory layout:

```text
plugins/com.example.hello/
├─ plugin.json
└─ web/
   └─ index.html
```

`plugin.json`:

```json
{
  "kind": "web",
  "id": "com.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "apiVersion": "2.2",
  "stateVersion": 1,
  "entry": "web/index.html"
}
```

A page running as the plugin's local top-level origin receives `window.papertodo`:

```html
<!doctype html>
<meta charset="utf-8">
<button id="hello">Hello</button>
<script>
  papertodo.paper.setTitle('Hello');
  papertodo.paper.setHeaderText('Hello Plugin');
  papertodo.paper.setCapsulePresentation({
    preferredWidth: 0,
    plainText: 'Hello',
    components: [{ kind: 'text', text: 'Hello', fill: true }]
  });

  document.querySelector('#hello').addEventListener('click', () => {
    papertodo.saveState({ clickedAt: Date.now() });
  });
</script>
```

During development, copy `plugin.json` and `web/` into the matching `plugins/<id>/` directory. **PaperTodo does not provide plugin-level hot reload. Restart PaperTodo after installing, deleting, or modifying plugin files.** `PaperBodyContext.Body.RequestReload()` rebuilds only the current Body session; it does not rescan manifests, replace Native DLLs, or restart the provider Runtime.

### 1.2 Minimal Native plugin

A Native project uses .NET 10 + WPF and references:

```text
PaperTodo.Plugin.Abstractions/PaperTodo.Plugin.Abstractions.csproj
```

Example project configuration:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\PaperTodo.Plugin.Abstractions\PaperTodo.Plugin.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

The entry assembly must contain exactly one public, non-abstract `IPaperBodyPlugin` implementation with a public parameterless constructor:

```csharp
using System.Windows;
using System.Windows.Controls;
using PaperTodo.Plugin;

public sealed class HelloPlugin : IPaperBodyPlugin
{
    public IPaperBodySession Create(PaperBodyContext context) =>
        new Session(context);

    private sealed class Session : IPaperBodySession
    {
        public Session(PaperBodyContext context)
        {
            View = new TextBlock
            {
                Text = "Hello PaperTodo",
                Margin = new Thickness(16)
            };
            context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
            {
                PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
                PlainText = "Hello",
                Components =
                [
                    new PaperCapsuleComponent
                    {
                        Kind = PaperCapsuleComponentKind.Text,
                        Text = "Hello",
                        Fill = true
                    }
                ]
            });
        }

        public FrameworkElement View { get; }
        public void Dispose() { }
    }
}
```

Native `plugin.json`:

```json
{
  "kind": "native",
  "id": "com.example.hello-native",
  "name": "Hello Native",
  "version": "1.0.0",
  "apiVersion": "2.2",
  "stateVersion": 1,
  "entry": "HelloPlugin.dll"
}
```

`plugin.json` is the single source of truth for Native plugin metadata. The entry DLL no longer redeclares the ID, name, version, protocol version, state version, capabilities, or background-runtime requirements; it implements behavior only.

### 1.3 Build and install a Native plugin

The repository provides a shared script:

```powershell
.\plugin-samples\Build-And-Install-NativePlugin.ps1 `
  -ProjectPath .\plugin-samples\PaperTodo.Plugin.SampleClock\PaperTodo.Plugin.SampleClock.csproj
```

The script:

- runs a Release / `win-x64` / framework-dependent publish;
- places the sibling `plugin.json` into the final package;
- removes PDB/XML files, the WebView2 loader, and shared assemblies already provided by the host;
- preserves the target plugin's existing `.runtime/` directory;
- installs to `plugins/<plugin ID>/`.

Exit PaperTodo before replacing a Native plugin. Once a Native plugin is loaded into the CLR, it cannot be safely hot-replaced in the current process. Restart PaperTodo after modifying or deleting it.

## 2. Directory and Deployment Boundaries

Repository directories have distinct responsibilities:

- `plugin-samples/`: plugin source code, source-side `plugin.json`, samples, and build scripts;
- `plugins/`: final built plugins that PaperTodo can load directly;
- `plugins/data/`: host-managed plugin settings, provider Runtime state, and per-paper frontend state;
- `plugins/<id>/.runtime/`: plugin-managed caches or independent long-lived data.

Neither local PaperTodo publish output nor GitHub Releases bundle plugins. Plugins are distributed independently.

Typical layout:

```text
plugins/
├─ data/
│  └─ com.example.weather.json
└─ com.example.weather/
   ├─ plugin.json
   ├─ web/
   │  ├─ index.html
   │  ├─ mini.html
   │  └─ runtime.html       # default provider Runtime entry; manifest runtime may rename it
   ├─ WeatherPlugin.dll
   ├─ WeatherPlugin.deps.json
   ├─ plugin-private dependencies / native libraries
   └─ .runtime/
```

`data` and `builtin.markdown` are reserved host IDs. A plugin ID must contain 3–120 ASCII letters, digits, `.`, `_`, or `-`. Directories beginning with `.` or `_` are not discovered.

A final Native plugin directory should contain only what is required at runtime. Do not ship unnecessary PDB/XML files, and do not duplicate host-provided shared assemblies such as `PaperTodo.Plugin.Abstractions`, Windows SDK / WinRT, or WebView2 shared assemblies.

## 3. `plugin.json`

The current manifest supports:

| Field | Description |
| --- | --- |
| `kind` | `web` or `native` |
| `id` | Unique plugin ID; the directory name must match |
| `name` | Display name; falls back to the ID when empty |
| `description` | Plugin description |
| `version` | Plugin version; must parse as `Version` |
| `apiVersion` | Use `"2.2"` for new plugins; `"2.1"` remains load-compatible |
| `stateVersion` | Target version for host-managed JSON; used by both per-paper frontend state and provider Runtime state; at least 1 |
| `maxPaperInstances` | Optional; maximum number of real Papers for the same provider. Defaults to `1`; `0` means unlimited. Hidden/collapsed Papers still count |
| `entry` | Web main page or Native entry DLL; must stay inside the plugin directory |
| `miniEntry` | Optional, Web only; dedicated Edge Mini page |
| `miniSize` | Optional; used only with `miniEntry`; preferred Mini size |
| `miniMaxSize` | Optional; maximum Mini capacity promised by the plugin, used for bounded host-capacity planning |
| `runtime` | Optional, Web Runtime only; at most one Runtime per provider. Defaults to `runtime.html` next to `entry` |
| `capabilities` | Optional: `textZoom`, `noteLinks`, and the lifecycle capability `runtime` |
| `permissions` | Optional Paper/Todo/Note Workspace permissions |
| `advancedSettings` | Optional, default `false`; enables a dedicated full settings page when `true` |
| `primarySettings` | Optional; effective only with `advancedSettings: true`; shows the first 1–3 settings directly on the plugin card; defaults to 3 |
| `settingCategories` | Optional; effective only with `advancedSettings: true`; declares categories for the full settings page and optional `left` / `right` column placement |
| `settings` | Optional global settings rendered and stored by the host; settings may specify `category` in advanced mode |
| `startupPaper` | Optional; automatically creates/restores a plugin Paper according to user settings |

`maxPaperInstances` is a Paper/provider-level product constraint and applies equally to Native and Web plugins. If an update lowers the limit below the number of existing instances, the host does not delete existing Papers; it only blocks creation of additional ones.

Unknown `capabilities` or `permissions` cause the plugin to be rejected. `runtime` is a provider-level background lifecycle declaration; it does not become a body flag in `PaperBodyCapabilities`.

### 3.1 Web `entry` / `miniEntry`

Both `entry` and `miniEntry` must remain inside the plugin directory. `miniEntry` must also stay inside the static directory rooted at the Web `entry`.

```json
{
  "kind": "web",
  "id": "com.example.weather",
  "name": "Weather",
  "version": "1.0.0",
  "apiVersion": "2.2",
  "stateVersion": 1,
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 },
  "miniMaxSize": { "width": 360, "height": 240 }
}
```

`miniSize` cannot be declared without `miniEntry`. A Web plugin that declares `miniMaxSize` must also have `miniEntry`. `miniSize` cannot exceed `miniMaxSize`.

### 3.2 `startupPaper`

A plugin can let a boolean setting control whether a plugin Paper is automatically created or restored on startup:

```json
{
  "startupPaper": {
    "enabledSetting": "autoStart",
    "instanceKey": "main",
    "presentation": "capsule",
    "title": "Weather"
  },
  "settings": [
    {
      "id": "autoStart",
      "type": "boolean",
      "name": "Show automatically on startup",
      "default": false
    }
  ]
}
```

Constraints:

- `enabledSetting` must reference a boolean setting in the same manifest;
- `instanceKey` must be 1–80 ASCII letters, digits, `.`, `_`, or `-`;
- `presentation` must be `capsule`, `expanded`, or `hidden`; `hidden` is a Protocol 2.2 feature and creates/restores the real Paper as a Runtime owner without showing its surface on normal startup;
- `title` is limited to 120 characters;
- creation timing, deduplication, and restoration are managed by the host; the plugin only declares intent;
- if the user has converted the originally auto-created Paper to another provider/type, the host does not forcibly reclaim it or silently create another copy.

### 3.3 `runtime` (2.1)

Declare a Runtime when the plugin must continue running independently of the Body/Mini UI lifecycle:

```json
"capabilities": ["runtime"]
```

There is one lifecycle model:

- on normal visible startup, `startupPaper` first creates/restores a real plugin Paper according to settings;
- when the provider has at least one real Paper, **one** Runtime starts; it is disposed when the final Paper leaves that provider;
- hiding, collapsing, Body rebuilds, Mini reclamation, or having no current `PaperWindow` does not affect the Runtime;
- there is no "background per Paper" contract. Multiple Papers still share the single Runtime and use `PaperId` to distinguish logical instances;
- if a plugin truly needs multiple workers, threads, subprocesses, or isolation domains, it manages them inside its Runtime;
- Native plugins declare `runtime` and implement `IPaperPluginRuntimeProvider`; Web plugins use manifest `runtime` to select the background entry, defaulting to `runtime.html` next to `entry`;
- restart PaperTodo after modifying plugin files.

## 4. Plugin Runtime Model

Native and Web plugins use the same model:

```text
Plugin provider
├─ Runtime ×0/1                 # single backend
│  ├─ Settings
│  ├─ provider State
│  ├─ Papers[paperId]           # N logical instances
│  ├─ Workspace
│  └─ Global Top Bar / Shortcuts
└─ Paper ×N
   ├─ Body                      # full frontend
   └─ Mini                      # lightweight frontend
```

Runtime is the backend, Paper is a logical instance, and Body/Mini are two frontends for the same Paper. Body and Mini do not need to own state directly for each other. Send messages to the Runtime when long-lived business state must change.

### 4.1 `PaperBodyContext.Paper`

A simple plugin without a Runtime may set its title, header, and capsule directly through `Paper`. Once `runtime` is declared, long-lived presentation is authored only by the Runtime through `context.Papers`; writes from Body/Mini to those long-lived presentation fields are no longer authoritative.

### 4.2 `PaperBodyContext.Body`

This belongs to the full body surface: `Controls`, `Theme`, `SetInputClaims(...)`, `MarkDirty()`, `OpenExternal(...)`, and `RequestReload()`. Body is a frontend and should not carry background business lifecycle responsibility once collapsed or hidden.

### 4.3 `PaperBodyContext.Runtime`

Body/Mini use the same thin command channel to send business messages to the provider Runtime:

```csharp
context.Runtime.Post(message);
```

Web equivalent:

```js
await papertodo.runtime.post(message);
```

The call reports only whether the current Runtime accepted the message. PaperTodo does not provide a business-level ACK, persistent message bus, automatic retry, or exactly-once delivery.

### 4.4 `PaperBodyContext.TopBar` / `Workspace`

The Paper Top Bar belongs to the current Body session. The Global Top Bar belongs to the provider Runtime. Workspace is the controlled Paper/Todo/Note API shared by both sides and is authorized by manifest `permissions`.

### 4.5 Body/Mini lifecycle

Body/Mini are frontends that may be created, hidden, rebuilt, and destroyed repeatedly. Native `IPaperBodySession.OnVisibilityChanged` / `OnPresentationChanged` describe frontend surface state only; they are no longer background keep-alive mechanisms. Logic that must continue when no UI exists belongs in the Runtime.

### 4.6 Provider Runtime

Native:

```csharp
public sealed class MyPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    public IPaperBodySession Create(PaperBodyContext context) => new Body(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new Runtime(context);
}
```

`PaperPluginRuntimeContext` provides:

- `Settings`: current global settings plus change subscriptions;
- `State`: one persistent JSON document owned by the provider Runtime;
- `Papers`: list logical Papers for the current provider, set long-lived presentation by `paperId`, send messages to Body, and receive Paper add/remove/frontend messages. At Runtime startup, call `List()` for the initial snapshot before `Subscribe(...)` for increments; subscriptions do not replay already-existing Papers;
- `Workspace`;
- `GlobalTopBar` / `GlobalShortcuts`;
- `TodoActions` / `TopBarLabels`: Protocol 2.1 host-rendered contributions.

A Web Runtime receives the corresponding `papertodo.settings`, `papertodo.state`, `papertodo.papers`, `papertodo.workspace`, and root `papertodo.request(method, params)`. `state` reads/writes provider Runtime state; `papers` manages only logical Papers belonging to the current provider; `workspace` accesses all PaperTodo data allowed by permissions. One provider creates only one hidden Runtime WebView.

This means the two `papers.list` calls have different meanings: `papertodo.papers.list()` returns the current provider's Runtime Paper snapshot only, while `papertodo.workspace.request('papers.list')` returns Workspace Papers visible under current permissions. Root `papertodo.request(...)` is the low-level Runtime transport; normal code should prefer the grouped APIs above to avoid confusing methods with similar names.

Runtime state and Body/Mini per-paper frontend state are different data domains. A Runtime may keep `instances[paperId]` inside provider state; Body `saveState` stores only that Paper's frontend state and does not compete with Runtime for the same writer.

## 5. State, Settings, and `.runtime`

### 5.1 Host-managed state

Host-managed state for each plugin is stored at:

```text
plugins/data/<plugin ID>.json
```

It contains:

- `settings`: shared by all Papers belonging to the plugin;
- `runtime`: one backend state document for the provider Runtime;
- `papers`: Body/Mini frontend state keyed by Paper ID;
- each Paper state is limited to **10 MiB of UTF-8 JSON**, and the provider Runtime state is limited to **20 MiB of UTF-8 JSON**.

The manifest's single `stateVersion` is the target version for both independent state domains, but they do not share JSON and do not overwrite each other.

A Native Body session uses:

```csharp
context.StateJson
context.StateVersion
context.TargetStateVersion
context.SaveStateJson(json)
```

Commit state to the host immediately after it changes; do not rely only on session `Commit()`. When saved Native Body state is older than the manifest `stateVersion`, the host calls `IPaperBodyPlugin.MigrateState(...)` before creating the session.

Web Body/Mini use:

```js
papertodo.saveState(nextState);
papertodo.registerStateProvider(() => currentState);
```

`initialize` for Body, Mini, and Web Runtime contains the `state`, `stateVersion`, and `targetStateVersion` for that surface's own state domain. Web plugins are responsible for normalizing old shapes into the current shape and saving after a real migration. Native Runtime does not call the Body's `MigrateState(...)`; instead compare `context.State.StateVersion` / `TargetStateVersion` and explicitly call `State.Save(...)` after migrating. If saved state is newer than the target version, Body is not created and Runtime does not start; the host does not guess a downgrade path. Do not overwrite old state with an empty object merely because parsing failed.

### 5.2 Recovery behavior

If the host cannot read the normal plugin data file:

- it preserves the original file;
- the current process continues from empty plugin state;
- later writes go to a stable `<plugin ID>.json.recovered` file;
- once `.recovered` exists, later runs prefer it.

A plugin-data failure does not invalidate PaperTodo's core `data.json`.

### 5.3 Global settings

The host supports `boolean`, `string`, `number`, `select`, `shortcut`, and, in Protocol 2.2, `action`. `action` is a host-rendered command button rather than stored settings data. Host-owned `paper.*` actions execute directly; a custom action ID requires `runtime` and is delivered through the existing Runtime action handler, without a shortcut binding. The remaining setting types continue to use the single settings storage/read-write protocol; the two modes below affect host presentation only.

For Protocol 2.1 shortcuts plus Protocol 2.2 `action` buttons, host `paper.*` actions, and custom Runtime action rules, see [`PROTOCOL-2.1-SHORTCUTS.md`](PROTOCOL-2.1-SHORTCUTS.md).

When `advancedSettings` is omitted or `false`, existing behavior remains unchanged: up to three `quick: true` settings are displayed directly on the plugin card, while the rest expand/collapse **inside the same card** through "More settings". If no setting is marked `quick`, the host does not guess which settings are primary.

Only `"advancedSettings": true` enables the new advanced-settings mode. The plugin card automatically shows the first three entries in `settings`; when more exist, "More settings" opens a dedicated full settings page. `primarySettings: 1..3` can override how many settings are shown directly. Settings on the full page may specify `category`; matching category names are grouped automatically. Top-level `settingCategories` may assign a category to `column: "left"` or `"right"`; omit the column to let the host place it automatically. The host first tries a single column and switches to two columns only when vertical space is insufficient and there are multiple blocks that can actually be distributed. A category is never split across columns.

```json
{
  "advancedSettings": true,
  "primarySettings": 2,
  "settingCategories": [
    { "name": "General", "column": "left" },
    { "name": "Network", "column": "right" },
    { "name": "Debug" }
  ],
  "settings": [
    { "id": "enabled", "type": "boolean", "name": "Enabled", "category": "General" },
    { "id": "mode", "type": "select", "name": "Mode", "category": "General", "options": [
      { "value": "auto", "name": "Auto" },
      { "value": "manual", "name": "Manual" }
    ] },
    { "id": "timeout", "type": "number", "name": "Timeout", "category": "Network" },
    { "id": "debug", "type": "boolean", "name": "Debug logging", "category": "Debug" }
  ]
}
```

A Native paper session reads its initial settings from `SettingsJson` and receives updates through `OnSettingsChanged`. A Web body reads from `initialize.settings` and receives `settingsChanged`.

Runtime does not borrow the paper-session settings lifecycle. Native code may read `PaperPluginRuntimeContext.Settings.Json` at any time or subscribe through `Settings.Subscribe(...)`. A Web Runtime receives the startup snapshot in `initialize.settings`, receives later `settingsChanged` events, and may also call `await papertodo.settings.get()` to fetch the latest values.

### 5.4 `.runtime/`

`.runtime/` is not part of the host-managed per-paper state protocol. It is appropriate for:

- a WebView2 Profile;
- rebuildable caches;
- large local indexes;
- plugin-private data that must outlive any individual Paper.

The plugin owns `.runtime/` format versioning, atomic writes, corruption recovery, and capacity control. Do not write ordinary single-Paper UI/business state to both `.runtime/` and `plugins/data/`, or you will create two authoritative copies of the same state.

### 5.5 Public application Settings API (2.2)

This optional capability controls **PaperTodo's user preferences**, not a provider's own `context.Settings`, `SettingsJson`, state, or `.runtime/`. Native Body and provider Runtime expose `context.SettingsApi` (`IPaperSettingsApi`); Web Body, Mini, and Runtime expose `papertodo.settingsApi`. No new required method is added to `IPaperTodoHostApi`.

Declare `settings.read` for List/Get and `settings.update` for Set. Permissions are checked before mutation.

```csharp
var links = context.SettingsApi.Get("todo.paper_links");
var todoSettings = context.SettingsApi.List("todo");
// Only in response to a requested settings change:
context.SettingsApi.Set("todo.paper_links", true);
```

```javascript
const links = await papertodo.settingsApi.get('todo.paper_links');
const catalog = await papertodo.settingsApi.list('todo');
await papertodo.settingsApi.set('todo.paper_links', true);
```

MCP provides `list_settings(category?)`, `get_setting(id)`, and `set_setting(id, value)`. List/Get require MCP enabled; Set requires full writes. Codex's separately authorized automatic-MCP activation can enable the existing MCP write/delete permissions. Disabling `mcp.enabled` completes the current response before its transport stops.

List returns an array. Each entry has a stable `id`, `category`, localized `title`, `type`, current `value`, caller-specific `writable`, `requires_restart`, and optional `unavailable_reason`, `min`, `max`, `step`, `max_length`, and `options`. Set returns `setting`, `previous_value`, and `changed`; assigning the current value is a no-op. Query the catalog instead of guessing CLR property names.

The catalog includes general/language/startup, privacy, appearance/fonts/background preferences, Note/Todo/title/capsule options, edge browsing, reminders, window/focus/interaction options, scripts, MCP, and global shortcuts. Values are saved preferences: dependent settings may remain selected but have no effect until their parent feature is enabled. Shortcuts expose `.gesture` and `.enabled`; each edge sequence uses a modifier chord plus 1 and applies that prefix to keys 1–9. Active shortcut-editor drafts/recording and registration conflicts are reported instead of silently overriding them. Language reports `requires_restart: true` and does not force a restart.

Settings are an explicit typed catalog. Per-paper geometry/content, live queue state, migration fields, plugin-private settings, file contents and one-shot UI commands such as import/export/reset are **not** setting values. These retain their existing APIs/owners; no raw `AppState` reflection endpoint is exposed. Future public settings must be explicitly registered with validation and effects.

All three adapters share one service. Core preferences synchronously save through `StateStore`, restore their previous values and coupled state on save failure, then refresh relevant UI/runtime paths. Startup registration and background preferences retain their existing independent persistence owners. Errors include `setting_not_found`, `invalid_setting_value`, `setting_dependency`, `settings_busy`, `setting_read_only`, `shortcut_conflict`, and `save_failed`. A committed setting is not reported as failed merely because a later UI refresh failed.

Todo linking remains available through the existing Workspace update request (`UpdateLinkedPaper=true`, `LinkedPaperId=null` unlinks). MCP adds `update_todo(clear_linked_paper=true)` for unambiguous unlinking; it conflicts with a non-null `linked_paper_id`. Omitting both leaves the link unchanged. Before creating a long-detail Note for a Todo, query `todo.paper_links`; when disabled or unknown, skip that approach unless the user explicitly requests a settings change.


## 6. Workspace Permissions and Data API

A manifest may declare:

```text
papers.read
papers.observe
papers.create
papers.delete

todos.read
todos.observe
todos.append
todos.update
todos.delete

notes.read
notes.observe
notes.append
notes.replace
```

Native paper sessions use `PaperBodyContext.Workspace`; Native Runtime uses `PaperPluginRuntimeContext.Workspace`; Web body/mini/Runtime all call `papertodo.workspace.request(method, params)` through their respective bridges.

Web data methods:

```text
papers.list
papers.get
papers.create
papers.delete

todos.list
todos.append
todos.update
todos.setReminder
todos.delete

notes.get
notes.write
```

Permission combinations that are easy to miss:

- creating a Note with initial body content requires `notes.append` in addition to `papers.create`;
- creating/appending a Todo with completion state, reminder, or `linkedPaperId` also requires `todos.update`;
- `todos.setReminder` uses `todos.update`;
- `notes.write` append/fill-blank operations use `notes.append`, while replace uses `notes.replace`;
- a paper-session plugin cannot delete the Paper that hosts its own active session; Runtime has no host Paper and is not subject to this single-Paper self-deletion restriction.

Observe permissions are independent of Read permissions. Without the matching read permission, events may still be delivered under observe permission, but the host removes sensitive fields.

Native paper session:

```csharp
using var subscription = context.Workspace.Subscribe(
    new PaperTodoEventFilter
    {
        Kinds = new HashSet<PaperTodoEventKind>
        {
            PaperTodoEventKind.TodoChanged
        },
        ExcludeOwnOperations = true
    },
    evt => { /* refresh model */ });
```

Web body:

```js
const dispose = papertodo.onHostEvent(
  ['todo.changed'],
  event => console.log(event),
  { excludeOwnOperations: true }
);
```

Subscribable events are `paper.created`, `paper.changed`, `paper.deleted`, `todo.created`, `todo.changed`, `todo.deleted`, and `note.changed`. Subscriptions become invalid automatically when the session becomes invalid or is disposed; plugins should still unsubscribe promptly when a listener is no longer needed.

### 6.1 Body read/write boundary

Top Bar does not provide a second `GetBodyText/SetBodyText` data path. Continue to use Workspace when reading or writing a target Paper:

- Markdown Note: `notes.get` + `notes.write`, governed by `notes.read` / `notes.append` / `notes.replace`;
- Todo: use structured `todos.*` APIs instead of pretending a Todo is a Markdown string;
- custom plugin body: body data remains owned by that provider's own state/capability. The host does not pretend every body is text.

Plugin Workspace and MCP share the same `PaperCommandService` business boundary, so save behavior, failure rollback, UI reconciliation, and event ordering do not get a second duplicate implementation just because the entry path differs.

### Cross-paper presentation (API 2.2)

Target `apiVersion: "2.2"` to control any existing paper by exact ID, including Todo, Markdown and other providers' papers. No extra presentation permission is required. Discover IDs separately with `papers.read`; presentation responses do not expose titles or content.

Native Body and Runtime both expose the optional `context.WorkspacePresentation` (`IPaperWorkspacePresentationApi`), without adding required members to the existing Workspace interface:

```csharp
context.WorkspacePresentation.ShowPaper(paperId, activate: false);
context.WorkspacePresentation.HidePaper(paperId);
context.WorkspacePresentation.TogglePaperVisibility(paperId, activate: false);
context.WorkspacePresentation.ExpandPaper(paperId, activate: false);
context.WorkspacePresentation.CollapsePaper(paperId);
context.WorkspacePresentation.TogglePaperCollapsed(paperId, activate: false);
context.WorkspacePresentation.ActivatePaper(paperId);
```

Web Body, Mini and Runtime use the existing Workspace request channel:

```js
await papertodo.workspace.request('papers.show', { paperId, activate: false });
await papertodo.workspace.request('papers.hide', { paperId });
await papertodo.workspace.request('papers.toggle', { paperId, activate: false });
await papertodo.workspace.request('papers.expand', { paperId, activate: false });
await papertodo.workspace.request('papers.collapse', { paperId });
await papertodo.workspace.request('papers.toggleCollapsed', { paperId, activate: false });
await papertodo.workspace.request('papers.activate', { paperId });
```

Show retains the current folded state; Expand also reveals the paper; Collapse retains visibility and never reveals a hidden paper. Activate reveals a hidden paper and requests focus without forcing expansion. `activate` defaults to `true` for show/expand/toggle operations. The host retains normal capsule eligibility checks; unavailable collapse returns `presentation_unavailable` instead of silently enabling a feature. Missing IDs return `paper_not_found`, never create a paper.

The response is `{ paper_id, is_visible, is_collapsed }`: logical state after request processing, **not animation completion or a synchronous disk-save guarantee**. Existing host transitions and their normal persistence scheduling remain authoritative. Hide never deletes content or removes the Runtime's real Paper owner. Toggles invert state: do not blindly retry an uncertain toggle; read the current state first. Native Runtime background calls are marshalled to the UI dispatcher and disposed contexts are rejected.

The session-scoped 2.1 `context.Presentation` / `papertodo.paper.*` API remains unchanged and still controls only its own host paper through the existing session-scoped API. Web popups do not gain a generic Workspace API.

MCP exposes `show_paper`, `hide_paper`, `toggle_paper_visibility`, `expand_paper`, `collapse_paper`, `toggle_paper_collapsed` and `activate_paper`, with `paper_id` and the same optional `activate` semantics. They require the MCP master switch but no additional full-write or delete permission. `list_papers` and `get_paper` return `is_collapsed` as well as `is_visible`.

## 7. Top Bar Extensions (2.1)

**PaperTodo always owns the Top Bar WPF tree, button sizing, placement, theme, hover behavior, DPI, and responsive layout. Plugins contribute action descriptors only.** Plugins cannot inject `FrameworkElement`, Button, WebView, or arbitrary Top Bar controls.

Top Bar has two explicit owners:

- **Paper**: `PaperBodyContext.TopBar` / body session. Shown only on the plugin Paper hosting the current session; up to 4 actions per session;
- **Global**: `PaperPluginRuntimeContext.GlobalTopBar` / provider Runtime. Shown on every PaperTodo Paper; up to 256 Global actions per provider Runtime. The Global runtime requires at least one real plugin Paper for its provider, but no Paper needs to be visible, expanded, or have a live body session.

Global actions are ordered by `Priority`, with larger numbers first. Ties are stable: first by provider Runtime registration order, then by declaration order inside the plugin. **PaperTodo's own host actions do not participate in this numeric priority space and always have a higher, non-overridable priority than plugin actions.** When width is insufficient, plugin contributions yield first; declaring many Global actions cannot push host actions out before plugin actions.

On normal visible startup, `startupPaper` is processed first. It may create/restore a real plugin Paper, after which the host starts Global runtimes according to the final set of real Papers. During runtime, the first real Paper starts the Runtime and the final Paper being deleted or switched to another provider disposes it. Deleting, hiding, or collapsing a non-final Paper does not remove Global actions.

A Global click contains `TargetPaperId`, `TargetPaperType`, and `TargetBodyProviderId`. `TargetBodyProviderId` is meaningful only for Notes; non-Note targets such as Todos return an empty string. Use these values to read or modify target Markdown/Todo data through Runtime Workspace. Top Bar itself does not own a business-data API.

### 7.1 Icons

The host can render two icon types:

1. `Character`: 1–8 UTF-16 characters; control characters are not allowed;
2. `SvgPath`: one SVG/WPF Path Data string, up to 4096 characters.

Full `<svg>`, `filter`, `image`, scripts, or arbitrary SVG DOM are not accepted.

SVG supports two render modes:

- `Fill`: the host fills the path with the current button foreground color;
- `Stroke`: the host strokes the path with the current button foreground color; `strokeWidth` must be between 0.1 and 4.0.

Button chrome, hit target, hover, disabled state, theme colors, and responsive collapsing are always controlled by PaperTodo.

### 7.2 Native Paper action

```csharp
context.TopBar.SetActionHandler(invocation =>
{
    // Callback for an action on the current Paper.
});

context.TopBar.SetPaperActions(
    [
        new PaperTopBarAction
        {
            Id = "refresh",
            Icon = PaperTopBarIcon.Character("↻"),
            ToolTip = "Refresh"
        }
    ],
    PaperHostTopBarActions.NewNotePaper);
```

A plugin Paper may request hiding only:

```text
NewTodoPaper
NewNotePaper
```

These values represent the host's "Create Todo" / "Create Note" actions; they are not a promise that those actions will always map to two separate physical buttons. Host lifeline behavior such as close, pin, title drag, and window lifecycle cannot be removed by a plugin.

### 7.3 Native Global action

Global actions are not registered from `PaperBodyContext`. After declaring `runtime`:

```csharp
public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context)
{
    context.GlobalTopBar.SetActionHandler(invocation =>
    {
        var target = context.Workspace.GetPaper(invocation.TargetPaperId);
        // Perform an operation against the target Paper.
    });

    context.GlobalTopBar.SetActions([
        new PaperTopBarAction
        {
            Id = "inspect-current",
            Icon = PaperTopBarIcon.SvgPath(
                "M3,3 L13,3 13,13 3,13 Z M6,8 L10,8",
                PaperTopBarSvgRenderMode.Stroke,
                1.5),
            ToolTip = "Inspect current Paper",
            Priority = 100
        }
    ]);

    return new Runtime();
}
```

Both `SetPaperActions(...)` and `GlobalTopBar.SetActions(...)` use replace semantics; pass an empty array for an empty action set. Disposing a Paper session automatically removes its Paper contribution. Disposing a provider Runtime automatically removes its Global contribution.

### 7.4 Web Paper action

A Web body registers Paper scope only:

```js
await papertodo.request('topbar.paper.set', {
  actions: [
    {
      id: 'refresh',
      icon: { kind: 'character', value: '↻' },
      toolTip: 'Refresh'
    }
  ],
  hiddenHostActions: ['newNotePaper']
});
```

Only the current ready body document can register actions, and a contribution is bound to that document generation. Navigation, renderer failure, replacement of the body WebView, or session disposal automatically removes the old Paper contribution. Calling `topbar.global.set` from Body returns `global_topbar_app_runtime_only`.

### 7.5 Web Global Runtime

Manifest:

```json
{
  "apiVersion": "2.2",
  "entry": "web/index.html",
  "runtime": "web/background.html",
  "capabilities": ["runtime"]
}
```

`runtime` may be omitted. When omitted, it defaults to `runtime.html` next to `entry`. An explicit path is still relative to the plugin directory and must stay inside the Web `entry` static directory.

When the provider has at least one real plugin Paper, the Runtime page receives:

```js
papertodo.surface;                    // 'runtime'
papertodo.workspace.request(method, params);
papertodo.settings.get();
papertodo.state.get();
papertodo.state.save(state);
papertodo.papers.list();
papertodo.papers.setTitle(paperId, title);
papertodo.papers.setHeaderText(paperId, text);
papertodo.papers.setCapsulePresentation(paperId, presentation);
papertodo.papers.postBody(paperId, message);
papertodo.globalTopBar.setActions(actions);
papertodo.request(method, params);    // root transport
papertodo.onEvent(listener);
```

Example:

```js
const settings = await papertodo.settings.get();

await papertodo.globalTopBar.setActions([
  {
    id: 'inspect-current',
    icon: {
      kind: 'svgPath',
      value: 'M3,3 L13,3 13,13 3,13 Z M6,8 L10,8',
      renderMode: 'stroke',
      strokeWidth: 1.5
    },
    toolTip: 'Inspect current Paper',
    priority: 100
  }
]);

papertodo.onEvent(async message => {
  if (message.type !== 'topBarActionInvoked') return;
  const paper = await papertodo.workspace.request('papers.get', {
    paperId: message.action.targetPaperId
  });
});
```

Runtime is an independent backend surface and does not receive `paper`, `body`, or `mini` presentation APIs. Runtime-document navigation, renderer failure, disappearance of the final real plugin Paper, or Runtime disposal all remove Global actions. Web Mini cannot register Global Top Bar actions either.

See `PaperTodo.Plugin.TopBarWeb` for a complete runnable example.

### 7.6 Protocol 2.1 Todo and Top Bar presentation extensions

A Protocol 2.1 provider Runtime can also publish two lightweight contribution types to the host:

- `TodoActions`: host-rendered action buttons on existing Todo rows. On click, the plugin receives the target Paper/Todo and latest `TodoSnapshot`;
- `TopBarLabels`: non-clickable, host-rendered labels on existing Paper Top Bars.

Web Runtime equivalents:

```js
papertodo.todoActions.set(paperId, todoId, actions);
papertodo.topBarLabels.set(paperId, labels);
```

Both contribution types exist only for the current Runtime lifecycle and do not become long-lived business state. See `PaperTodo.Plugin.Protocol21Web` for a complete example.

### 7.7 Paper context menu and lightweight popups

These capabilities extend existing entry points only: **text actions in the Paper context menu, Note image reads, and temporary popups at the click position**. They remain optional Protocol 2.1 interfaces and do not start a Runtime merely because a plugin is installed.

| Native entry | Web entry | Boundary |
| --- | --- | --- |
| `runtimeContext.PaperActions` | Runtime `papertodo.paperActions` | `papers.read`; register text menu items for a specific Paper |
| `context.NoteAssets` | `papertodo.noteAssets` | `notes.read`; read images owned by a specific Note |
| `context.Popups` | Body / Runtime `papertodo.popups` | Plugin renders content; host performs one-time positioning and closes on focus loss |

Here `context` may be either `PaperBodyContext` or `PaperPluginRuntimeContext`. Context-menu entries are registered only by an existing provider Runtime using `SetActionHandler` / `SetActions(paperId, actions)`. Web uses `paperActions.set(paperId, actions)` plus the `paperActionInvoked` event. Menu items contain only `Id` and `Text`, displayed in declaration order. They do not require icons and provide no tooltip, sort-priority, or new Top Bar capability. Replacing or clearing a registration invalidates its old callbacks, and Runtime shutdown clears all registrations.

A right-click returns `PaperActionInvocation`: the action ID, current Paper snapshot, and `Position`. **Existing Top Bar click events also gain an optional `Position` property while preserving the original five-parameter constructor and deconstruction method**, so already-compiled plugins do not need to adopt the new capability. Position is a physical-screen-pixel snapshot at click time, not a control reference or long-lived positioning token. Pass it back to the host rather than treating it as WPF logical coordinates yourself.

Native usage, registered while an existing Runtime initializes; the example content class is implemented by the plugin:

```csharp
var menus = context.PaperActions!;
var popups = context.Popups!;
menus.SetActionHandler(action =>
    popups.Open(action.Position, new() { Width = 320, Height = 240 },
        popup => new MyPanel(popup, action.Paper.Id)));
menus.SetActions(paperId, [new() { Id = "details", Text = "View Details" }]);
```

`MyPanel` implements `IPaperPluginPopupContent` and returns a fresh, unparented WPF `View` created on the factory's thread. The host does not accept another `Window` and does not migrate a control already mounted in the body. The factory receives the current theme, existing `Controls.ApplySelectStyle`, and a close callback. Once content is adopted, the host disposes it exactly once on normal close or creation failure. Theme changes are delivered through `OnThemeChanged`.

Popup dimensions use WPF logical pixels. On first display, the host converts them for the target monitor and constrains the popup to the work area. Each session / Runtime owns at most one popup; opening another replaces the previous one. **The popup takes focus; moving focus outside closes it, while interacting with text boxes, buttons, or dropdowns inside does not.** After display, it no longer follows the source control or window. The host does not additionally own Esc handling; popup content handles that itself. Popups are reclaimed when the plugin ends or the creator Web page navigates. There is no standalone persistent window, window numbering, nested popup chain, or long-lived anchor API.

Web usage from the creator page (`panel.html` is relative to the directory containing manifest `entry`):

```javascript
papertodo.onEvent(event => {
  if (event.type === 'paperActionInvoked') {
    const action = event.action;
    papertodo.popups.open(action.position, {
      entry: 'panel.html', width: 320, height: 240,
      data: { paperId: action.paper.id }
    }).catch(console.error);
  }
});
await papertodo.paperActions.set(paperId, [{ id: 'details', text: 'View Details' }]);
```

The existing `topBarActionInvoked` event may likewise pass `event.action.position` to `popups.open`. A normal Body cannot register `paperActions`, but it does have `popups` and `noteAssets`. The creator may call `popups.close()`.

The popup itself is a small frontend, not a second Runtime. Its page receives only:

```javascript
const { data, theme } = await papertodo.ready;
// Fetch large images by ID instead of placing Base64 in initial data.
const image = await papertodo.noteAssets.readImage(data.paperId, imageId);
document.querySelector('img').src = `data:${image.mime};base64,${image.bytes}`;
await papertodo.popup.post({ selected: imageId }); // creator receives popupMessage
papertodo.popup.close();
```

A popup can read initial data, receive theme events, read permitted Note images, send messages to its creator, and close itself. **It does not directly receive general Workspace write access, menu registration, or the ability to open another popup.** Business writes are handled by the creator. Initial data and each message are limited to 64 KiB. The entry accepts local HTML only; external navigation, new windows, and downloads are cancelled. Navigation invalidates old message credentials. Recoverable Web helper-process failures do not close the popup; main renderer or browser failures close it and report `popupError` to the creator.

`NoteAssets.ReadImage(paperId, imageId)` returns the MIME type and separately encoded bytes from `PaperNoteImage`; Web exposes `bytes` as Base64. It reuses `notes.read`, can read only images owned by built-in Markdown Notes, and has a 16 MiB limit per read. Missing, corrupt, or ownership-mismatched assets report `asset_not_found`; oversized assets report `asset_too_large`. The API does not expose image writes, disk paths, or internal storage objects, and it does not promise an atomic export snapshot of an entire Note.

## 8. Capsule Presentation

### 8.1 Host-rendered standard capsule

Plugins may submit `PaperCapsulePresentation`. The shell, close area, hover behavior, drag behavior, edge docking, cross-monitor movement, DPI handling, and input remain owned by PaperTodo.

A standard presentation contains at most three components, in declaration order:

- `text`
- `glyph`
- `statusDot`
- `progressRing`
- `progressBar`

Components support `fill`, fixed `width`, `tone`, and custom `color`.

Width:

- Native: `PreferredWidth = PaperCapsulePresentation.AutomaticWidth`
- Web: `preferredWidth: 0`

Both mean the host should measure natural width from content. A positive number is the plugin's preferred full content-slot width in DIP; the host still constrains it to the legal range.

Always provide meaningful `plainText` for temporary text-only surfaces and safe fallback rendering.

### 8.2 Native custom WPF capsule

A Native session may implement `IPaperCapsuleViewProvider`. `CreateCapsuleView(PaperCapsuleViewContext)` creates separate WPF content Views for `Regular` and `Docked`.

Rules:

- the two surfaces must return different WPF objects;
- Views must be fresh, unparented, and pure WPF;
- `Window`, `HwndHost`, `WindowsFormsHost`, WebView2, and controls with an existing parent are not accepted;
- custom capsule content does not own mouse input;
- the host still owns shell, close area, click, right-click, drag, hover, docking, and DPI;
- failure or `null` falls back to the standard capsule;
- automatic width is resolved from the standard presentation first, then the final slot size is passed to the custom View;
- the host caches the View for the same session/geometry. Update live state in place instead of continuously rebuilding the View.

Web plugins do not get custom WPF capsules; they use the host-rendered standard presentation.

## 9. Edge Mini

Edge Mini is a quick-browse surface. **Plugins contribute content; PaperTodo always owns the Edge window, queue placement, card frame, size normalization, and input routing.** Do not create a plugin-owned Edge HWND or duplicate the host's queue/geometry algorithms.

Current paths:

1. Native dedicated mini: `IPaperMiniViewProvider`;
2. Web dedicated mini: manifest `miniEntry`;
3. without a dedicated mini, the host builds a read-only preview from the custom/standard capsule or `plainText`.

### 9.1 Mini size

`PaperMiniViewSize` / `miniSize` describes the **complete card size including host frame and close area**, in DIP.

The protocol does not define a fixed 120×90 minimum or 480×420 maximum. Declared `width` / `height` must be **positive finite numbers**; the host constrains only the final size against the current monitor's available work area.

`miniMaxSize` is an optional **upper capacity declaration**: the plugin promises that its Mini will not request width or height beyond that value under the current protocol. This lets the host prepare bounded capacity instead of reserving an unnecessarily large WebView/HWND/hosting surface. It does not give the plugin authority over the final window size; PaperTodo's monitor work area and host rules still determine the final bounds. Web plugins declaring `miniMaxSize` must also have `miniEntry`, and `miniSize` may not exceed it. Native plugins may declare the same upper bound in the manifest. If omitted, the host uses a compatibility capacity policy.

Default preferred size:

```text
320 × 220 DIP
```

Built-in Todo / Markdown may continue using their own renderer envelope and visual default size; those values are not plugin protocol limits. Native `PreferredMiniViewSize` may change with session state. When no queue-proxy transaction is active, the host can resize the bounded host directly. If size changes during queue translation, growth may be deferred briefly until that transaction finishes. **Avoid high-frequency size changes while a Mini is visible, and do not use Preferred Size as an animation parameter**, because resizing can trigger host/native relayout and cause transient jank.

### 9.2 Native dedicated mini

Implement `IPaperMiniViewProvider`. A dedicated mini may share a business model with the body, but it must use a different WPF control instance.

Rules:

- `CreateMiniView` must return a fresh / unparented / pure-WPF tree;
- `Window`, `HwndHost`, WindowsFormsHost, and WebView2 are not accepted;
- returning `null` or failing to create the mini does not fail the body session;
- `OnMiniViewVisibilityChanged(false)` is sent when retraction begins; the plugin may pause refresh/input but should keep the last rendered content available to finish the exit transition;
- the Edge host does not take keyboard focus, so Mini must not depend on text input.

Standard WPF Button, selector, scrollbar, Thumb, Hyperlink, and similar controls can consume pointer input. Other custom elements may declare:

```csharp
PaperMiniViewInteraction.SetConsumesPointer(element, true);
```

### 9.3 Web dedicated mini

Web manifest:

```json
{
  "entry": "web/index.html",
  "miniEntry": "web/mini.html",
  "miniSize": { "width": 300, "height": 190 },
  "miniMaxSize": { "width": 360, "height": 240 }
}
```

`miniEntry` uses an independent WebView2. Keep it local and lightweight; do not load the full remote application again.

Publication flow: transparent content placeholder → delayed cold WebView2 initialization → current-document `initialize` → `papertodo.mini.ready()` after the first real layout → current-generation challenge → `CompositionTarget.Rendering` publication boundary → publish the Web surface only if generation/visibility still match.

Therefore, do not assume the Web surface becomes synchronously visible as soon as `mini.ready()` is called, and do not rely on the old capsule to act as a placeholder for the Web page.

Pointer input in Web Mini belongs to PaperTodo by default. Mark only local controls that truly need the Web page to handle click/press/drag:

```html
<button type="button" data-papertodo-interactive>Pause</button>
```

The host mirrors the current DOM rectangles of marked elements into the WPF input layer. Unmarked regions continue to handle host interactions such as opening the full Paper or dragging Edge Mini. Do not indiscriminately mark the entire root node as interactive.

Body and Mini share the same host-managed state/settings. When either surface calls `saveState`, the other receives `stateChanged`; the receiver must not blindly write the same value back with `saveState`, or it will create an echo loop.

Web Mini does not receive keyboard focus and **cannot register Top Bar actions**.

## 10. Web Plugins

### 10.1 Local origin and bridge

The directory containing Web `entry` is the local static root. Keeping it at `web/` is recommended so `.runtime/` is not exposed through page-resource mapping.

The plugin's own local top-level page runs at:

```text
https://<plugin-id>.papertodo.local/
```

Only the plugin's local **top-level document** receives `window.papertodo`. Remote pages, iframes, and other origins do not receive the host bridge.

PaperTodo treats Web plugins as trusted content. Same-origin frames/popups and permissions retain normal WebView2 behavior. External top-level navigation and external new-window requests are handed to the system default application. Normal HTTP/HTTPS downloads are preferably handed to the system default browser, while session-local downloads such as `blob:` and `data:` keep normal WebView2 behavior.

### 10.2 Body bridge

A body page can use:

```js
papertodo.surface;                    // 'body'
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.setInputClaims(['escapeKey', 'contextMenu']);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.runtime.post(message);
papertodo.workspace.request(method, params);
papertodo.request(method, params);           // Paper Top Bar uses root transport
papertodo.onHostEvent(types, listener, options);
papertodo.onEvent(listener);
```

The host sends:

```text
initialize
stateChanged
settingsChanged
activated
deactivated
visibilityChanged
presentationChanged
themeChanged
typographyChanged
dpiChanged
commitRequested
cancelInteractions
hostResponse
hostEvent
hostSubscriptionError
topBarActionInvoked
runtimeMessage
```

`initialize` includes the current surface, paper/provider IDs, API/state versions, state, settings, permissions, theme, Runtime visibility, and presentation visibility.

### 10.3 Mini bridge

A `miniEntry` page can use:

```js
papertodo.surface;                    // 'mini'
papertodo.mini.ready();
papertodo.saveState(state);
papertodo.registerStateProvider(fn);
papertodo.paper.setTitle(text);
papertodo.paper.setHeaderText(text);
papertodo.paper.setCapsulePresentation(value);
papertodo.body.markDirty();
papertodo.body.openExternal(url);
papertodo.runtime.post(message);
papertodo.workspace.request(method, params);
papertodo.onEvent(listener);
```

Mini does not have the body's `setInputClaims` and cannot register Top Bar actions. Keyboard focus never belongs to Edge Mini. Pointer input belongs to the host by default and is delegated to the page only inside elements marked with `data-papertodo-interactive`. Mini host-request routing accepts only the Workspace data methods currently listed; it does not automatically inherit future host capabilities based on method-name prefixes.

Body/Mini `runtime.post(...)` reports only whether the current provider Runtime accepted the message. It is not a business ACK or persistent message queue.

### 10.4 Plugin Runtime bridge

A Web plugin declaring `runtime` creates an independent Runtime surface while the provider has at least one real plugin Paper. Manifest `runtime` may select a custom entry; otherwise it defaults to `runtime.html` next to `entry`:

```js
papertodo.surface;                    // 'runtime'
papertodo.workspace.request(method, params);
papertodo.settings.get();
papertodo.state.get();
papertodo.state.save(state);
papertodo.papers.list();
papertodo.papers.setTitle(paperId, title);
papertodo.papers.setHeaderText(paperId, text);
papertodo.papers.setCapsulePresentation(paperId, presentation);
papertodo.papers.postBody(paperId, message);
papertodo.globalTopBar.setActions(actions);
papertodo.todoActions.set(paperId, todoId, actions);
papertodo.todoActions.clear(paperId, todoId);
papertodo.todoActions.clearAll();
papertodo.topBarLabels.set(paperId, labels);
papertodo.topBarLabels.clear(paperId);
papertodo.topBarLabels.clearAll();
papertodo.request(method, params);    // root transport
papertodo.onEvent(listener);
```

Runtime `initialize` contains `settings`, provider Runtime `state` plus its versions, and the current provider `papers` snapshot. Later setting changes produce `settingsChanged`; Paper add/remove events and Body messages produce `paperEvent`. Runtime does not receive paper-session APIs such as `paper`, `body`, `mini`, or `saveState`, and it does not have the Web `onHostEvent` subscription bridge.

`papertodo.papers.postBody(...)` / Native `Papers.PostToBody(...)` only attempts delivery to the current live Body. Its return value tells whether that Body currently accepts the message; a Web Body may temporarily buffer until its document is ready. It is not a persistent message queue, business ACK, or guarantee of final delivery.

### 10.5 State writes

Call `saveState` promptly after each real paper-session state mutation. `registerStateProvider` merely gives the host a best-effort way to flush current state at boundaries such as `commitRequested`, page hide, or unload; it should not be the only durability mechanism.

After Runtime state mutations, promptly call Native `context.State.Save(json)` or Web `papertodo.state.save(state)`. Runtime has no `registerStateProvider` / `commitRequested` flush path.

## 11. Native Plugins

Native plugins are fully trusted / unsandboxed .NET/WPF code and run with the current user's PaperTodo permissions.

Key rules:

- `IPaperBodyPlugin` is a factory and must not hold session state for a specific Paper;
- each Paper body session uses a new plugin object / `IPaperBodySession`;
- without `runtime`, manifest-only discovery does not load the Native DLL merely because PaperTodo starts;
- with `runtime`, a provider Runtime is created only while the provider has at least one real plugin Paper;
- Runtime and paper sessions are separate objects/lifetimes; a specific paper session cannot be treated as the authority for Global runtime behavior;
- the entry assembly must contain exactly one valid `IPaperBodyPlugin` implementation;
- restart PaperTodo after plugin files change or are deleted;
- private dependencies and native libraries belong in the plugin's self-contained directory;
- do not duplicate host-shared assemblies;
- timers, tasks, subscriptions, Top Bar contributions, and external resources must all end with their owning session/runtime lifecycle.

For select controls that need host-consistent visuals, use `PaperBodyContext.Body.Controls` rather than copying PaperTodo's internal popup/theme/DPI details.

## 12. Choosing a Sample Project

| Sample | Focus |
| --- | --- |
| `PaperTodo.Plugin.Protocol21Web` | **Protocol 2.1 contribution-focused sample**: Todo row actions, Top Bar labels, latest `TodoSnapshot` |
| `PaperTodo.Plugin.TopBarWeb` | **Protocol 2.2 Top Bar/action-focused sample**: body Paper action + Web Runtime Global action, character/Stroke SVG icons, target Paper context, Workspace reuse |
| `PaperTodo.Plugin.SampleClock` | Main Native sample: settings, background updates, standard capsule, custom WPF capsule, dedicated WPF mini |
| `PaperTodo.Plugin.OfficialClockWeb` | Main Web sample: body/mini pages, `miniEntry`, state/settings synchronization, startup Paper, background updates |
| `PaperTodo.Plugin.FocusTimer` | Stateful Native interaction: body and dedicated mini share a timer model; start/pause/resume directly from Mini |
| `PaperTodo.Plugin.ReviewArchive` | Workspace reads/observe plus combined use of plugin state and long-lived data |
| `PaperTodo.Plugin.CloudGenshin` | Body containing WebView2/native child content: keep the full remote app in Body and use a separate pure-WPF status panel for Edge Mini |

When starting a new plugin, copy the smallest structure from the sample closest to your target. Do not combine every sample capability at once.

## 13. Common Mistakes

### Manifest / Runtime

- targeting `apiVersion: "2.0"` or earlier for a new plugin; the current host supports `2.1`–`2.2`, and new plugins should target `2.2`;
- plugin directory name does not match `id`;
- `id` uses invalid characters or the reserved host IDs `data` / `builtin.markdown`;
- Web declares `runtime`, but the default `runtime.html` is missing, or an explicit `runtime` path is missing/outside the Web `entry` static directory;
- Native declares `runtime` but does not implement `IPaperPluginRuntimeProvider`;
- assuming a Runtime starts just because a plugin is installed while it has zero real plugin Papers;
- treating `runtime` as `startupPaper`: Runtime does not create a Paper; `startupPaper` is what creates a startup Paper;
- modifying plugin files and expecting the current process to rescan/hot-replace them automatically; restart PaperTodo instead;
- declaring `miniSize` without `miniEntry`;
- `miniSize` exceeds `miniMaxSize`, or Web declares `miniMaxSize` without `miniEntry`;
- Web `miniEntry` escapes the `entry` static directory;
- maintaining a second copy of id/version/API/state/runtime metadata inside the entry DLL; `plugin.json` is the sole authority;
- more than three `quick: true` settings;
- `startupPaper.enabledSetting` does not point to a boolean setting;
- unknown `capabilities` / `permissions`.

### Top Bar

- treating Top Bar as a Workspace data API;
- passing `FrameworkElement` / Button / full SVG to PaperTodo instead of an action descriptor;
- duplicate action IDs, IDs longer than 64 characters, or invalid characters;
- more than 4 Paper actions in one session, or more than 256 Global actions in one provider Runtime;
- assuming plugin `Priority` can outrank host buttons; host actions always have higher priority;
- passing a complete `<svg>` instead of Path Data;
- using a non-finite `strokeWidth` or a value outside 0.1–4.0 for `Stroke`;
- attempting to hide host lifeline actions such as close, pin, or drag;
- implementing a second body-mutation path for Top Bar instead of reusing Workspace;
- registering Global actions from paper body / Web Mini instead of Runtime;
- binding Global contribution lifetime to one body session, Paper visibility, or expanded state instead of provider real-Paper existence;
- assuming the previous document's Paper contribution remains valid after a Web body reload.

### WPF surface

- returning the same WPF element for body, Regular capsule, Docked capsule, or Mini;
- returning a control that already has a parent;
- treating `Window`, `HwndHost`, WindowsFormsHost, or WebView2 as a migratable/custom Mini tree;
- placing clickable buttons inside a read-only custom capsule;
- making Edge Mini depend on keyboard focus.

### Web Mini

- assuming `miniSize` still has a fixed 120×90–480×420 protocol range;
- treating `miniMaxSize` as authority over the final window size;
- forgetting `data-papertodo-interactive` on local controls that need Web-side click handling;
- marking the entire root node interactive just to take over input;
- assuming the Web surface becomes synchronously visible after `mini.ready()`.

### State

- saving only in `Commit()` or page unload;
- echoing `stateChanged` straight back through `saveState`, creating a body/mini loop;
- storing ordinary per-paper state in both `plugins/data` and `.runtime/`;
- overwriting old data with an empty object after a migration parse failure;
- exceeding 10 MiB for a single Paper state.

### Workspace / lifecycle

- calling Workspace without permission;
- treating observe permission as read permission;
- a paper session trying to delete the active Paper that hosts itself;
- declaring `runtime` when no background lifecycle is needed;
- allowing timers/tasks/subscriptions to continue after session/runtime disposal;
- blocking the UI thread for a long time inside a Native Runtime Top Bar callback;
- letting the plugin own Edge HWND, queue placement, frame, or geometry.

## 14. Before Submitting a Plugin

- new `plugin.json` targets the current `apiVersion: "2.2"`; use `2.1` only when intentionally staying within the 2.1 contract;
- all metadata lives only in `plugin.json`; the Native entry DLL provides behavior only;
- when declaring `runtime`: Native implements `IPaperPluginRuntimeProvider`; Web provides `runtime.html` next to `entry` by default, or uses `runtime` to select another entry inside the same Web static directory;
- when Runtime needs plugin settings, use its own `context.Settings.Json` + `Settings.Subscribe(...)` / `papertodo.settings.get()` + `settingsChanged`; do not borrow a hidden paper session;
- on normal visible startup, `startupPaper` decides first whether to create/restore a real plugin Paper; Runtime always starts according to the final real-Paper count;
- Global Top Bar is registered only by Runtime: deleting a non-final Paper must not remove it, while deleting/converting the final Paper must remove it. Global action `Priority` expresses plugin-internal ordering; host actions always rank higher;
- Native passes the shared build/install script;
- final `plugins/<id>/` contains no PDB/XML files or duplicate shared assemblies;
- the build script does not accidentally delete `.runtime/`;
- Web body and Mini state/settings synchronization has no echo loop;
- Web Mini marks only local elements that truly need pointer input with `data-papertodo-interactive`;
- if `miniMaxSize` is declared, it truthfully covers the maximum size Mini may request, and `miniSize` does not exceed it;
- Top Bar submits host-rendered descriptors only; Paper contributions disappear with the session and Global contributions disappear with Runtime;
- capsule provides meaningful `plainText`;
- every custom WPF surface is fresh / unparented / pure WPF;
- Edge Mini does not depend on keyboard input;
- declare only permissions / `runtime` that are actually needed;
- 0↔1 Runtime ownership is correct when switching providers or deleting Papers; exiting PaperTodo fully removes Runtime and Global Top Bar contributions.