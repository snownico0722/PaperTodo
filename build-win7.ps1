param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$legacyVersion = "3.31-win7BestEffort"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $repoRoot "PaperTodo.csproj"
$notifyIconProject = Join-Path $repoRoot "vendor\wpf-notifyicon\src\NotifyIconWpf\NotifyIconWpf.csproj"

if (-not (Test-Path $projectFile)) {
    throw "PaperTodo.csproj was not found next to build-win7.ps1."
}

if (-not (Test-Path $notifyIconProject)) {
    throw "vendor/wpf-notifyicon is missing. Run: git submodule update --init --recursive"
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("PaperTodo-win7-" + [Guid]::NewGuid().ToString("N"))
$publishDir = Join-Path $repoRoot "输出\PaperTodo-v$legacyVersion"
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false

function Copy-SourceTree {
    param([string]$Source, [string]$Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $excluded = @(".git", ".vs", "bin", "obj", "输出")
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($excluded -contains $item.Name) {
            continue
        }

        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Replace-ExactBlock {
    param(
        [string]$RelativePath,
        [string]$OldBlock,
        [string]$NewBlock
    )

    $path = Join-Path $stage $RelativePath
    if (-not (Test-Path $path)) {
        throw "Win7 patch target is missing: $RelativePath"
    }

    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $old = $OldBlock.Replace("`r`n", "`n")
    $new = $NewBlock.Replace("`r`n", "`n")

    $first = $text.IndexOf($old, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Win7 patch no longer matches $RelativePath. Refuse to build instead of silently producing an unpatched package."
    }

    $second = $text.IndexOf($old, $first + $old.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "Win7 patch matched more than once in $RelativePath. Refuse to build."
    }

    $patched = $text.Substring(0, $first) + $new + $text.Substring($first + $old.Length)
    [IO.File]::WriteAllText($path, $patched, $utf8NoBom)
}

function Assert-NoUnsupportedDpiImports {
    $patterns = @(
        '\[DllImport\("shcore\.dll"\)\]\s*private static extern int GetDpiForMonitor',
        '\[DllImport\("user32\.dll"\)\]\s*private static extern uint GetDpiForWindow',
        '\[DllImport\("user32\.dll"\)\]\s*private static extern IntPtr SetThreadDpiAwarenessContext'
    )

    foreach ($file in Get-ChildItem -LiteralPath $stage -Filter "*.cs" -Recurse -File) {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($pattern in $patterns) {
            if ([regex]::IsMatch($text, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
                $relative = $file.FullName.Substring($stage.Length).TrimStart([char[]]@('\', '/'))
                throw "Unsupported post-Win7 DPI import remains in staged source: $relative"
            }
        }
    }
}

try {
    Write-Host "[Win7] staging source..."
    Copy-SourceTree -Source $repoRoot -Destination $stage

    # Force a Windows 7-safe system-DPI manifest for this package only.
    Copy-Item -LiteralPath (Join-Path $stage "win7.fork\app.manifest") -Destination (Join-Path $stage "app.manifest") -Force

    $workAreaOld = @'
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
'@
    $workAreaNew = @'
    private static int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY) =>
        Win7Compatibility.GetDpiForMonitor(hmonitor, dpiType, out dpiX, out dpiY);

    private static uint GetDpiForWindow(IntPtr hwnd) =>
        Win7Compatibility.GetDpiForWindow(hwnd);
'@
    Replace-ExactBlock -RelativePath "WindowWorkAreaHelper.cs" -OldBlock $workAreaOld -NewBlock $workAreaNew

    $windowNativeOld = @'
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
'@
    $windowNativeNew = @'
    private static IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext) =>
        Win7Compatibility.SetThreadDpiAwarenessContext(dpiContext);

    private static uint GetDpiForWindow(IntPtr hwnd) =>
        Win7Compatibility.GetDpiForWindow(hwnd);
'@
    Replace-ExactBlock -RelativePath "WindowNative.cs" -OldBlock $windowNativeOld -NewBlock $windowNativeNew

    $paperWindowNativeOld = @'
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
'@
    $paperWindowNativeNew = @'
    private static uint GetDpiForWindow(IntPtr hwnd) =>
        Win7Compatibility.GetDpiForWindow(hwnd);
'@
    Replace-ExactBlock -RelativePath "PaperWindow.Native.cs" -OldBlock $paperWindowNativeOld -NewBlock $paperWindowNativeNew

    Assert-NoUnsupportedDpiImports

    if (Test-Path $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    $selfContainedValue = if ($SelfContained) { "true" } else { "false" }
    $compressionValue = if ($SelfContained) { "true" } else { "false" }
    $packageKind = if ($SelfContained) { "self-contained" } else { "no-runtime" }
    $releaseExeName = "PaperTodo-v$legacyVersion-$Runtime-$packageKind.exe"

    $arguments = @(
        "publish",
        (Join-Path $stage "PaperTodo.csproj"),
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContainedValue,
        "-p:Version=$legacyVersion",
        "-p:InformationalVersion=$legacyVersion",
        "-p:CETCompat=false",
        "-p:IncludeSourceRevisionInInformationalVersion=false",
        "-p:PublishSingleFile=true",
        "-p:PublishReadyToRun=false",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=$compressionValue",
        "-p:PublishTrimmed=false",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-p:EmbedAllSources=false",
        "-p:EmbedUntrackedSources=false",
        "-o", $publishDir
    )

    Write-Host "[Win7] publishing PaperTodo v$legacyVersion..."
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    $defaultExe = Join-Path $publishDir "PaperTodo.exe"
    if (-not (Test-Path -LiteralPath $defaultExe -PathType Leaf)) {
        throw "Win7 single-file publish did not produce PaperTodo.exe."
    }

    $releaseExe = Join-Path $publishDir $releaseExeName
    Move-Item -LiteralPath $defaultExe -Destination $releaseExe -Force

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].FullName -ne $releaseExe) {
        $names = ($publishedFiles | ForEach-Object { $_.FullName.Substring($publishDir.Length).TrimStart([char[]]@('\', '/')) }) -join ", "
        throw "Win7 package is not a true single-file publish. Files: $names"
    }

    Write-Host "[Win7] done: $releaseExe"
    Write-Host "[Win7] This is a best-effort compatibility build. Test on Windows 7 SP1 x64 before release."
}
finally {
    if (Test-Path $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
