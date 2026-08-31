# Windows 7 best-effort compatibility

This directory contains the small compatibility layer used by `build-win7.ps1`.
The normal 3.x source remains unchanged at build time; the compatibility script copies the repository to a temporary staging directory and patches only that staged copy.

The Win7 build currently replaces only APIs that do not exist on Windows 7:

- `GetDpiForMonitor` -> system DPI from `GetDeviceCaps`
- `GetDpiForWindow` -> system DPI from `GetDeviceCaps`
- `SetThreadDpiAwarenessContext` -> no-op
- PerMonitorV2 manifest -> Windows 7 system-DPI-aware manifest

The guarded replacements target the corresponding files under `src/`. If a source refactor makes an expected replacement stop matching, the build aborts instead of silently producing an unpatched package.

The compatibility version is derived from `PaperTodo.csproj`: for example, PaperTodo `3.31` becomes `3.31-win7BestEffort`.

## Build

Framework-dependent single file:

```powershell
.\build-win7.ps1
```

Self-contained single file (recommended for Windows 7 distribution):

```powershell
.\build-win7.ps1 -SelfContained
```

Output is written under:

```text
输出\PaperTodo-v<version>-win7BestEffort\
```

Windows 7 remains an unsupported .NET 10 platform. Treat this as a best-effort Windows 7 SP1 x64 build and validate it on real Windows 7 before publishing.
