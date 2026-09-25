param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ForceRebuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Join-Path $root "Cargo.toml"
$targetTriple = "x86_64-pc-windows-msvc"
$profile = if ($Configuration -eq "Release") { "release" } else { "debug" }
$output = Join-Path $root "bin/win-x64/papertodo_math.dll"

if (-not $ForceRebuild -and (Test-Path -LiteralPath $output -PathType Leaf)) {
    Write-Output "Precompiled papertodo_math.dll found, skipping Cargo build."
    exit 0
}

$precompiledBackup = $null
if ($ForceRebuild -and (Test-Path -LiteralPath $output -PathType Leaf)) {
    $precompiledBackup = "$output.precompiled"
    Copy-Item -LiteralPath $output -Destination $precompiledBackup -Force
    Remove-Item -LiteralPath $output -Force
}

try {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw "Rust/Cargo is required to build PaperTodo's native math library."
    }

    $arguments = @(
        "build",
        "--manifest-path", $manifest,
        "--target", $targetTriple
    )
    if ($Configuration -eq "Release") {
        $arguments += "--release"
    }
    if (Test-Path -LiteralPath (Join-Path $root "Cargo.lock") -PathType Leaf) {
        $arguments += "--locked"
    }

    & cargo @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Building PaperTodo's native math library failed."
    }

    $built = Join-Path $root "target/$targetTriple/$profile/papertodo_math.dll"
    if (-not (Test-Path -LiteralPath $built -PathType Leaf)) {
        throw "The native math build completed without producing $built."
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
    Copy-Item -LiteralPath $built -Destination $output -Force
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
