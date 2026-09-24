param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,
    [string]$ManifestPath,
    [string]$PaperTodoRoot,
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory
    )

    if ([System.IO.Path]::IsPathRooted($Value)) {
        return [System.IO.Path]::GetFullPath($Value)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $Value))
}

if ([string]::IsNullOrWhiteSpace($PaperTodoRoot)) {
    $PaperTodoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory ".."))
} else {
    $PaperTodoRoot = [System.IO.Path]::GetFullPath($PaperTodoRoot)
}

if (-not (Test-Path -LiteralPath (Join-Path $PaperTodoRoot "PaperTodo.csproj") -PathType Leaf)) {
    throw "PaperTodo repository root not found: $PaperTodoRoot"
}

$ProjectPath = Resolve-RepositoryPath -Value $ProjectPath -BaseDirectory $PaperTodoRoot
if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
    throw "Native plugin project not found: $ProjectPath"
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path (Split-Path -Parent $ProjectPath) "plugin.json"
} else {
    $ManifestPath = Resolve-RepositoryPath -Value $ManifestPath -BaseDirectory $PaperTodoRoot
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Native plugin manifest not found: $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
$pluginId = [string]$manifest.id
$entryName = [string]$manifest.entry
if ([string]$manifest.kind -ine "native") {
    throw "Build-And-Install-NativePlugin.ps1 only supports native plugins."
}
if ([string]::IsNullOrWhiteSpace($pluginId) -or
    $pluginId -notmatch '^[A-Za-z0-9._-]{3,120}$') {
    throw "plugin.json contains an invalid native plugin id: '$pluginId'"
}
if ([string]::IsNullOrWhiteSpace($entryName) -or
    [System.IO.Path]::GetFileName($entryName) -ne $entryName -or
    [System.IO.Path]::GetExtension($entryName) -ine ".dll") {
    throw "Native plugin entry must be a DLL file name in the plugin root: '$entryName'"
}

$pluginsRoot = [System.IO.Path]::GetFullPath((Join-Path $PaperTodoRoot "plugins"))
$targetDirectory = [System.IO.Path]::GetFullPath((Join-Path $pluginsRoot $pluginId))
$expectedPrefix = $pluginsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $targetDirectory.StartsWith(
        $expectedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Plugin output path escaped the repository plugins directory: $targetDirectory"
}

if (Get-Process -Name "PaperTodo" -ErrorAction SilentlyContinue) {
    throw "Please exit PaperTodo before replacing a native plugin."
}

$stagingDirectory = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    "PaperTodo.NativePlugin-" + [System.Guid]::NewGuid().ToString("N"))
$artifactsDirectory = Join-Path $stagingDirectory "artifacts"
$publishDirectory = Join-Path $stagingDirectory "publish"
$packageDirectory = Join-Path $stagingDirectory "package"
$nugetConfig = Join-Path $PaperTodoRoot "NuGet.Config"

try {
    New-Item -ItemType Directory -Force -Path $publishDirectory, $packageDirectory | Out-Null

    & dotnet restore $ProjectPath `
        -r $RuntimeIdentifier `
        --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) {
        throw "Native plugin restore failed with exit code $LASTEXITCODE."
    }

    & dotnet publish $ProjectPath `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained false `
        --no-restore `
        --artifacts-path $artifactsDirectory `
        -o $publishDirectory `
        /p:DebugType=none `
        /p:DebugSymbols=false `
        /p:GenerateDocumentationFile=false
    if ($LASTEXITCODE -ne 0) {
        throw "Native plugin build failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $publishDirectory -Force |
        Copy-Item -Destination $packageDirectory -Recurse -Force
    Copy-Item -LiteralPath $ManifestPath `
        -Destination (Join-Path $packageDirectory "plugin.json") -Force

    $sharedAssemblyNames = @(
        "PaperTodo.Plugin.Abstractions",
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET",
        "Microsoft.Web.WebView2.Core",
        "Microsoft.Web.WebView2.Wpf",
        "Microsoft.Web.WebView2.WinForms"
    )
    $sharedFileNames = foreach ($name in $sharedAssemblyNames) {
        "$name.dll"
        "$name.pdb"
        "$name.xml"
    }

    Get-ChildItem -LiteralPath $packageDirectory -File -Recurse -Force |
        Where-Object {
            $_.Extension -ieq ".pdb" -or
            $_.Name -ieq "WebView2Loader.dll" -or
            $sharedFileNames -contains $_.Name
        } |
        Remove-Item -Force

    Get-ChildItem -LiteralPath $packageDirectory -Filter "*.xml" -File -Recurse -Force |
        Where-Object {
            Test-Path -LiteralPath ([System.IO.Path]::ChangeExtension($_.FullName, ".dll"))
        } |
        Remove-Item -Force

    Get-ChildItem -LiteralPath $packageDirectory -Directory -Recurse -Force |
        Sort-Object FullName -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
        Remove-Item -Force

    $entryAssembly = Join-Path $packageDirectory $entryName
    $dependencyManifest = Join-Path $packageDirectory (
        [System.IO.Path]::GetFileNameWithoutExtension($entryName) + ".deps.json")
    foreach ($requiredFile in @($entryAssembly, $dependencyManifest)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Required plugin output is missing after cleanup: $requiredFile"
        }
    }

    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    Get-ChildItem -LiteralPath $targetDirectory -Force |
        Where-Object { $_.Name -ne ".runtime" } |
        Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $packageDirectory -Force |
        Copy-Item -Destination $targetDirectory -Recurse -Force

    Write-Host "Installed $pluginId to $targetDirectory"
} finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
