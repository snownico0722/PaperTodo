# PaperTodo MSIX packaging (3.x)

This directory contains the Microsoft Store/MSIX packaging experiment for the `3.x` line.

## Design

- The normal portable EXE build is unchanged and continues to keep its data beside the executable.
- The MSIX install directory is read-only, so the MSIX workflow rewrites app-relative data paths only inside the CI workspace before compilation. Packaged builds use `MsixDataDirectory.Current`, which resolves to `LocalApplicationData\PaperTodo` for a packaged process. Windows may further virtualize AppData for an MSIX package.
- The package is self-contained (`win-x64`) so users do not need to install a separate .NET Desktop Runtime.
- The workflow builds the native LMDB library, publishes PaperTodo, generates the required package logos from the existing PaperTodo PNG icon, creates the MSIX with `MakeAppx.exe`, self-signs it temporarily, and registers/unregisters it on the GitHub runner as a packaging validation step.
- Microsoft Store will replace the temporary CI signature during Store publishing.

## GitHub repository variables

Before submitting the package to Partner Center, reserve the PaperTodo product name and copy the Store identity values into repository variables:

- `MSIX_IDENTITY_NAME` — Package/Identity/Name from Partner Center.
- `MSIX_PUBLISHER` — Package/Identity/Publisher from Partner Center.
- `MSIX_PUBLISHER_DISPLAY_NAME` — public publisher display name.

If these variables are absent, CI uses the isolated test identity `PaperTodo.Test` / `CN=PaperTodo Test`. That package is useful for build validation but must not be submitted as the real Store product.

## Version mapping

PaperTodo's project version is mapped to the first three MSIX version fields and the fourth field is forced to `0`, because Microsoft Store reserves the fourth field. Examples:

- `3.31` -> `3.31.0.0`
- `3.31.1` -> `3.31.1.0`

## Artifact

Run **Actions -> Build MSIX -> Run workflow**. The artifact contains:

- `PaperTodo-v<version>-win-x64.msix`
- `PaperTodo-MSIX-test.cer` (temporary CI certificate for sideload testing only)
- `AppxManifest.used.xml`
- `MSIX_BUILD.txt`
