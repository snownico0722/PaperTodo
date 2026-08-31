# Windows 7 compatibility fork

This directory contains the small compatibility layer used by `build-win7.ps1`.
It does not modify the normal v3.31 source tree in place.

The Win7 build currently replaces only APIs that do not exist on Windows 7:

- `GetDpiForMonitor` -> system DPI from `GetDeviceCaps`
- `GetDpiForWindow` -> system DPI from `GetDeviceCaps`
- `SetThreadDpiAwarenessContext` -> no-op
- PerMonitorV2 manifest -> Windows 7 system-DPI-aware manifest

`build-win7.ps1` copies the repository to a temporary staging directory, applies exact guarded replacements there, checks that no targeted unsupported DPI imports remain, then publishes the staged tree. If a source refactor makes one of the expected replacements stop matching, the build aborts instead of silently producing an unpatched package.

## Build

```powershell
.\build-win7.ps1
```

Default output:

```text
输出\PaperTodo-v3.31-Win7-Preview
```

The default package is framework-dependent and targets `win-x64`. Use `-SelfContained` only for testing; Windows 7 remains an unsupported .NET 10 platform and the package still requires real Windows 7 SP1 x64 validation before release.
