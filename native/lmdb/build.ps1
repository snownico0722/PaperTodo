param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ForceRebuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDirectory = Join-Path $root "obj/win-x64-cloud"

$output = Join-Path $root "bin/win-x64/papertodo_lmdb.dll"
if (-not $ForceRebuild -and (Test-Path -LiteralPath $output -PathType Leaf)) {
    Write-Output "Precompiled papertodo_lmdb.dll found, skipping CMake build."
    exit 0
}

$precompiledBackup = $null
if ($ForceRebuild -and (Test-Path -LiteralPath $output -PathType Leaf)) {
    $precompiledBackup = "$output.precompiled"
    Copy-Item -LiteralPath $output -Destination $precompiledBackup -Force
    Remove-Item -LiteralPath $output -Force
}

try {
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
        throw "CMake is required to build PaperTodo's native LMDB library."
    }

    # Use the newest Visual Studio generator installed on the runner. windows-latest currently
    # advances independently (for example VS 2026), so pinning a generator makes releases brittle.
    cmake -S $root -B $buildDirectory -A x64
    if ($LASTEXITCODE -ne 0) {
        throw "Configuring PaperTodo's native LMDB library failed."
    }

    cmake --build $buildDirectory --config $Configuration --target papertodo_lmdb
    if ($LASTEXITCODE -ne 0) {
        throw "Building PaperTodo's native LMDB library failed."
    }

    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
        throw "The native LMDB build completed without producing $output."
    }
}
catch {
    if ($precompiledBackup -and (Test-Path -LiteralPath $precompiledBackup -PathType Leaf)) {
        Copy-Item -LiteralPath $precompiledBackup -Destination $output -Force
    }
    throw
}
finally {
    if ($precompiledBackup -and (Test-Path -LiteralPath $precompiledBackup -PathType Leaf)) {
        Remove-Item -LiteralPath $precompiledBackup -Force
    }
}
