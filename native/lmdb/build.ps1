param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDirectory = Join-Path $root "obj/win-x64"

$output = Join-Path $root "bin/win-x64/papertodo_lmdb.dll"
if (Test-Path -LiteralPath $output -PathType Leaf) {
    Write-Output "Precompiled papertodo_lmdb.dll found, skipping CMake build."
    exit 0
}

if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    throw "CMake is required to build PaperTodo's native LMDB library."
}

cmake -S $root -B $buildDirectory -A x64
if ($LASTEXITCODE -ne 0) {
    throw "Configuring PaperTodo's native LMDB library failed."
}

cmake --build $buildDirectory --config $Configuration --target papertodo_lmdb
if ($LASTEXITCODE -ne 0) {
    throw "Building PaperTodo's native LMDB library failed."
}

$output = Join-Path $root "bin/win-x64/papertodo_lmdb.dll"
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw "The native LMDB build completed without producing $output."
}
