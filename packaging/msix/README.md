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
- **TEST** — isolated `PaperTodo.Test` / `CN=PaperTodo Test` identity. CI self-signs this package and includes the temporary `.cer` so it can be deliberately validated and sideloaded on a real test machine.

Pull requests and ordinary `3.x` branch pushes build TEST packages. `v3*` tag pushes build STORE packages. A manual **Build MSIX** run lets you choose either kind and defaults to STORE.

Artifacts are named visibly so a TEST package cannot be mistaken for a Store submission:

- `PaperTodo-v<version>-win-x64-STORE.msix`
- `PaperTodo-v<version>-win-x64-TEST.msix`

## Runtime differences from the portable build

- The normal portable EXE is unchanged and keeps data beside the executable.
- A packaged Store build uses `%LOCALAPPDATA%\PaperTodo` as the application data root when running with package identity. This is decided by checked-in source (`AppDataDirectory`); CI does not rewrite C# source before compiling.
- On the first Store launch, if the Store data directory is empty, PaperTodo offers to import an existing portable `data.json`. It also copies the backup, LMDB note images, recovery JSON files, and optional custom icon/font files. Telemetry queues/markers are not copied.
- If the imported portable copy had PaperTodo registered in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, the Store build attempts to preserve that choice by enabling the package `windows.startupTask` and only then removes the old portable Run entry.
- Store startup toggling uses `Windows.ApplicationModel.StartupTask`; portable builds continue using the existing per-user Run registry entry.
- **Microsoft Store builds contain no telemetry implementation or telemetry resources.** The legacy `telemetryEnabled` JSON field remains readable only for data-file compatibility and has no telemetry effect in the Store build.
- The package is self-contained (`win-x64`), so Store users do not need a separate .NET Desktop Runtime.

## Version mapping

PaperTodo's project version is mapped to the first three MSIX version fields and the fourth field is forced to `0`:

- `3.31` -> `3.31.0.0`
- `3.31.1` -> `3.31.1.0`

## CI validation

CI requires:

1. native LMDB rebuild;
2. Store-flavor restore/publish (`PaperTodoStoreBuild=true`);
3. required payload files (`PaperTodo.exe`, `papertodo_lmdb.dll`);
4. absence of telemetry resource payloads;
5. manifest/logo generation;
6. successful `MakeAppx.exe pack`;
7. for TEST packages, successful temporary self-signing;
8. artifact upload with SHA-256 digest.

GitHub-hosted Windows Server runners are not a reliable interactive desktop for trusting a self-signed root, `Add-AppxPackage`, or Windows App Certification Kit testing. Signature-chain verification, installation, launch smoke testing, and WACK therefore live in `Validate-MSIX.ps1` and must be run on a real Windows test session.

## Real Windows validation / WACK

For the TEST artifact:

```powershell
.\Validate-MSIX.ps1 `
  -PackagePath .\PaperTodo-v3.31-win-x64-TEST.msix `
  -CertificatePath .\PaperTodo-MSIX-test.cer
```

The script:

1. unpacks and reads the manifest;
2. temporarily trusts the TEST certificate in CurrentUser;
3. runs `SignTool verify /pa /v`;
4. installs the package;
5. launches PaperTodo through its packaged AppUserModelId and checks that the process stays running;
6. removes the package/certificate again unless `-KeepInstalled` is used.

To include Windows App Certification Kit testing, run from an elevated PowerShell session:

```powershell
.\Validate-MSIX.ps1 `
  -PackagePath .\PaperTodo-v3.31-win-x64-TEST.msix `
  -CertificatePath .\PaperTodo-MSIX-test.cer `
  -RunWack
```

The script uses the `appcert.exe reset` and `appcert.exe test -packagefullname ... -reportoutputpath ...` flow. WACK needs a real signed-in Windows desktop session; it is intentionally not a GitHub-hosted CI gate.

## Store submission

For a real submission, use the **STORE** artifact. It already has the official Partner Center identity above and is intentionally unsigned for Partner Center. Do not upload a TEST artifact.
