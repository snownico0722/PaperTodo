param(
    [ValidateSet('All', 'SelfContained', 'NoRuntime')]
    [string]$Mode = 'All',

    [string]$OutputDirectory = 'artifacts/r2r'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'PaperTodo.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "PaperTodo.csproj not found at $projectPath"
}

if (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$project = [xml](Get-Content -LiteralPath $projectPath -Raw)
$versionNodes = $project.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1) {
    throw "PaperTodo.csproj must define exactly one <Version> element (found $($versionNodes.Count))."
}
$version = ([string]$versionNodes[0].InnerText).Trim()
if ([string]::IsNullOrWhiteSpace($version) -or $version -match '\s') {
    throw "PaperTodo.csproj <Version> is missing or contains whitespace: '$version'."
}

Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("papertodo-r2r-{0}" -f [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-DirectorySize([string]$Path) {
    $sum = (Get-ChildItem -LiteralPath $Path -File -Recurse | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return [long]0 }
    return [long]$sum
}

function New-R2RPackage([string]$Kind, [bool]$SelfContained) {
    $suffix = if ($SelfContained) { 'self-contained' } else { 'no-runtime' }
    $publishDir = Join-Path $tempRoot $Kind
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $args = @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '--no-restore',
        '-o', $publishDir,
        '/p:PublishSingleFile=false',
        '/p:PublishReadyToRun=true',
        '/p:PublishTrimmed=false',
        '/p:EmbedAllSources=false',
        '/p:EmbedUntrackedSources=false',
        '/p:DebugType=none',
        '/p:DebugSymbols=false'
    )
    Invoke-DotNet $args

    $exePath = Join-Path $publishDir 'PaperTodo.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "R2R publish did not produce PaperTodo.exe for $Kind."
    }

    $zipName = "PaperTodo-v$version-win-x64-r2r-multifile-$suffix.zip"
    $zipPath = Join-Path $OutputDirectory $zipName
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

    $zip = Get-Item -LiteralPath $zipPath
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $files = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
    [pscustomobject]@{
        kind = $Kind
        selfContained = $SelfContained
        readyToRun = $true
        singleFile = $false
        publishBytes = Get-DirectorySize $publishDir
        publishFiles = $files.Count
        zipName = $zip.Name
        zipBytes = [long]$zip.Length
        sha256 = $hash
    }
}

Push-Location $repoRoot
try {
    Invoke-DotNet @('restore', $projectPath, '-r', 'win-x64')

    $results = @()
    if ($Mode -in @('All', 'SelfContained')) {
        $results += New-R2RPackage -Kind 'sc-multifile-r2r' -SelfContained $true
    }
    if ($Mode -in @('All', 'NoRuntime')) {
        $results += New-R2RPackage -Kind 'fd-multifile-r2r' -SelfContained $false
    }

    $manifestPath = Join-Path $OutputDirectory 'r2r-packages.json'
    [ordered]@{
        version = $version
        rid = 'win-x64'
        configuration = 'Release'
        packages = $results
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    $results | Format-Table kind, publishFiles, publishBytes, zipBytes, zipName -AutoSize
    Write-Host "Manifest: $manifestPath"
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
