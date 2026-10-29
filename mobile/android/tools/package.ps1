$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distributionDirectory = Join-Path $projectDirectory 'dist'
$versionMatch = [regex]::Match((Get-Content -LiteralPath (Join-Path $projectDirectory 'app\build.gradle.kts') -Raw), 'versionName\s*=\s*"([0-9A-Za-z.+-]+)"')
if (-not $versionMatch.Success) { throw '未找到 Android versionName。' }
$artifactName = 'PaperTodo-Mobile-v' + $versionMatch.Groups[1].Value
$apk = Join-Path $distributionDirectory "$artifactName.apk"
if (-not (Test-Path -LiteralPath $apk)) { throw '请先运行 tools/build-android.ps1 -Mode Release。' }
if (-not (Test-Path -LiteralPath (Join-Path $distributionDirectory 'desktop-transfer\PaperTodoTransfer.exe'))) { throw '请先发布 Windows 传输工具到 dist/desktop-transfer。' }

function Write-Archive([string]$Destination, [object[]]$Entries) {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination }
    $archive = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $Entries) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Source, $entry.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
}

$sourceEntries = @(Get-ChildItem -LiteralPath $projectDirectory -Recurse -File -Force | ForEach-Object {
    $relative = $_.FullName.Substring($projectDirectory.Length + 1).Replace('\', '/')
    if ($relative -notmatch '(^|/)(\.git|\.gradle|\.local|\.idea|build|bin|obj|dist)(/|$)' -and
        $relative -notmatch '(^|/)local\.properties$|\.(keystore|jks|iml|user)$') {
        [PSCustomObject]@{ Source = $_.FullName; Name = "PaperTodoMobile/$relative" }
    }
})
$sourceZip = Join-Path $distributionDirectory "$artifactName-source.zip"
Write-Archive $sourceZip $sourceEntries

$deliveryEntries = @([PSCustomObject]@{ Source = $apk; Name = "$artifactName.apk" })
foreach ($name in @('INSTALL.md', 'README.md', 'VERIFICATION.md', 'LICENSE.md', 'NOTICE.md', 'THIRD-PARTY-NOTICES.md')) {
    $deliveryEntries += [PSCustomObject]@{ Source = (Join-Path $projectDirectory $name); Name = $name }
}
foreach ($directory in @('licenses', 'docs\screenshots')) {
    $path = Join-Path $projectDirectory $directory
    if (Test-Path -LiteralPath $path) {
        $deliveryEntries += Get-ChildItem -LiteralPath $path -Recurse -File | ForEach-Object {
            [PSCustomObject]@{ Source = $_.FullName; Name = $_.FullName.Substring($projectDirectory.Length + 1).Replace('\', '/') }
        }
    }
}
$transferDirectory = Join-Path $distributionDirectory 'desktop-transfer'
$deliveryEntries += Get-ChildItem -LiteralPath $transferDirectory -Recurse -File | Where-Object {$_.Extension -ne '.pdb'} | ForEach-Object {
    [PSCustomObject]@{ Source = $_.FullName; Name = 'desktop-transfer/' + $_.FullName.Substring($transferDirectory.Length + 1).Replace('\', '/') }
}
$deliveryZip = Join-Path $distributionDirectory "$artifactName-complete.zip"
Write-Archive $deliveryZip $deliveryEntries

$checksums = @($apk, $sourceZip, $deliveryZip) | ForEach-Object {
    (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_)
}
$checksums | Set-Content -LiteralPath (Join-Path $distributionDirectory 'SHA256SUMS.txt') -Encoding Ascii
Write-Output '已生成安装与迁移完整包、源码包和 SHA256SUMS.txt。源码包不含个人数据、缓存及签名密钥。'
