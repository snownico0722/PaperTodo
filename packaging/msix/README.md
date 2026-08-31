# PaperTodo MSIX packaging (3.x)

This directory contains the Microsoft Store/MSIX packaging path for the `3.x` line.

## Store identity

The Partner Center identity is public package metadata, not a secret:

- `Package/Identity/Name`: `snowtrigger.PaperTodo`
- `Package/Identity/Publisher`: `CN=71A31110-CB7B-4E95-A801-2E034D3DB464`
- `Package/Properties/PublisherDisplayName`: `snowtrigger`

The checked-in `AppxManifest.xml` contains these official values. CI only replaces them when it deliberately builds an isolated TEST package.

## Build flavors

The workflow has two explicit package kinds:

- **STORE** — official Partner Center identity, unsigned MSIX intended for Partner Center. Microsoft Store signs the package during certification/distribution.
- **TEST** — isolated `PaperTodo.Test` / `CN=PaperTodo Test` identity. CI self-signs this package and includes the temporary `.cer` so it can be deliberately sideloaded on a test machine.

Pull requests and ordinary `3.x` branch pushes build TEST packages. `v3*` tag pushes build STORE packages. A manual **Build MSIX** run lets you choose either kind and defaults to STORE.

Artifacts are named visibly so a TEST package cannot be mistaken for a Store submission:

- `PaperTodo-v<version>-win-x64-STORE.msix`
- `PaperTodo-v<version>-win-x64-TEST.msix`

## Runtime differences from the portable build

- The normal portable EXE is unchanged and keeps data beside the executable.
- A packaged Store build uses `%LOCALAPPDATA%\PaperTodo` for application-relative data/config/log/customization files. This is decided by the checked-in source (`AppDataDirectory`); CI does not rewrite C# source before compiling.
- On the first Store launch, if the Store data directory is empty, PaperTodo offers to import an existing portable `data.json`. It also copies the backup, LMDB note images, recovery JSON files, and optional custom icon/font files. Telemetry queues/markers are not copied.
- If the imported portable copy had PaperTodo registered in `HKCU\...\Run`, the Store build attempts to preserve that choice by enabling the package `windows.startupTask` and only then removes the old portable Run entry.
- Store startup toggling uses `Windows.ApplicationModel.StartupTask`; portable builds continue using the existing per-user Run registry entry.
- **Microsoft Store builds contain no telemetry implementation or telemetry resources.** The legacy `telemetryEnabled` JSON field remains readable only for data-file compatibility and has no effect in the Store build.
- The package is self-contained (`win-x64`), so Store users do not need a separate .NET Desktop Runtime.

## Version mapping

PaperTodo's project version is mapped to the first three MSIX version fields and the fourth field is forced to `0`:

- `3.31` -> `3.31.0.0`
- `3.31.1` -> `3.31.1.0`

## Validation

CI requires:

1. native LMDB rebuild;
2. Store-flavor restore/publish (`PaperTodoStoreBuild=true`);
3. required payload files (`PaperTodo.exe`, `papertodo_lmdb.dll`);
4. manifest/logo generation;
5. successful `MakeAppx.exe pack`;
6. for TEST packages, temporary self-signing **and** `SignTool verify` with the temporary root trusted only for that CI step;
7. artifact upload.

GitHub-hosted Windows runners are not a reliable active desktop session for `Add-AppxPackage`/Windows App Certification Kit testing. Use `Validate-MSIX.ps1` on a real Windows test machine. It installs and launches the TEST package, checks that PaperTodo starts, and can run WACK with:

```powershell
.\Validate-MSIX.ps1 `
  -PackagePath .\PaperTodo-v3.31-win-x64-TEST.msix `
  -CertificatePath .\PaperTodo-MSIX-test.cer `
  -RunWack
```

WACK requires an active user session and an elevated shell. The script uses the Microsoft-documented `appcert.exe reset` and `appcert.exe test -packagefullname ... -reportoutputpath ...` flow.

## Store submission

For a real submission, use the **STORE** artifact. It already has the official Partner Center identity above. Do not upload a TEST artifact.
