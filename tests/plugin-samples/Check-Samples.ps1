#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('PaperTodo-plugin-checks-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporaryRoot
Push-Location $repository
try {
    Write-Host '
    === Validate plugin manifests ==='
    $manifests = Get-ChildItem plugin-samples,plugins -Recurse -Filter plugin.json
    if (-not $manifests) {
      throw "No plugin manifests were found."
    }
    foreach ($manifest in $manifests) {
      Write-Host "Validating $($manifest.FullName)"
      $document = Get-Content $manifest.FullName -Raw -Encoding UTF8 |
        ConvertFrom-Json -Depth 100
      if ($document.apiVersion -notin @("2.1", "2.2")) {
        throw "$($manifest.FullName) must target a supported apiVersion (2.1 or 2.2)."
      }
    }

    Write-Host '
    === Validate Web source and runtime copies ==='
    $source = Join-Path $PWD "plugin-samples/PaperTodo.Plugin.OfficialClockWeb"
    $runtime = Join-Path $PWD "plugins/official.clock.web"
    $sourceFiles = @(
      Get-Item (Join-Path $source "plugin.json")
      Get-ChildItem (Join-Path $source "web") -File -Recurse
    ) |
      ForEach-Object {
        [pscustomobject]@{
          Path = [IO.Path]::GetRelativePath($source, $_.FullName)
          Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
      }
    $runtimeFiles = Get-ChildItem $runtime -File -Recurse |
      ForEach-Object {
        [pscustomobject]@{
          Path = [IO.Path]::GetRelativePath($runtime, $_.FullName)
          Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        }
      }
    $difference = Compare-Object $sourceFiles $runtimeFiles -Property Path,Hash
    if ($difference) {
      $difference | Format-Table | Out-String | Write-Host
      throw "Web plugin source and runtime copies are out of sync."
    }

    Write-Host '
    === Check Web settings action behavior ==='
    node ./tests/PaperTodo.ProtocolPolicyChecks/SettingActionSampleChecks.cjs
    if ($LASTEXITCODE -ne 0) { throw "Plugin sample behavior check failed: $LASTEXITCODE" }

    Write-Host '
    === Check Web plugin scripts ==='
    $htmlFiles = Get-ChildItem plugin-samples,plugins -Recurse -Filter *.html
    foreach ($html in $htmlFiles) {
      $content = Get-Content $html.FullName -Raw -Encoding UTF8
      $matches = [regex]::Matches(
        $content,
        '<script(?:\s[^>]*)?>([\s\S]*?)</script>',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
      for ($index = 0; $index -lt $matches.Count; $index++) {
        $temporary = Join-Path $temporaryRoot (
          "$($html.BaseName)-$index-" + [Guid]::NewGuid().ToString("N") + ".js")
        Set-Content $temporary $matches[$index].Groups[1].Value -Encoding UTF8
        node --check $temporary
        if ($LASTEXITCODE -ne 0) {
          throw "Plugin sample command failed: $LASTEXITCODE"
        }
      }
    }

    Write-Host '
    === Rebuild and verify native runtime outputs ==='
    $projects = Get-ChildItem -Path plugin-samples -Recurse -Filter *.csproj |
      Sort-Object FullName
    if (-not $projects) {
      throw "No native plugin sample projects were found."
    }
    foreach ($project in $projects) {
      Write-Host "Rebuilding $($project.FullName)"
      & ./plugin-samples/Build-And-Install-NativePlugin.ps1 `
        -ProjectPath $project.FullName
      if ($LASTEXITCODE -ne 0) {
        throw "Plugin sample command failed: $LASTEXITCODE"
      }
    }
    git diff --exit-code -- plugins
    if ($LASTEXITCODE -ne 0) {
      throw "Committed native plugin outputs do not match their source projects."
    }
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
