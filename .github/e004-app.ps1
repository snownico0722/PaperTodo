param([string]$Workspace = '.')
$ErrorActionPreference = 'Stop'
$Workspace = (Resolve-Path $Workspace).Path
$evidence = Join-Path $Workspace 'evidence'
$fixtures = Join-Path $env:RUNNER_TEMP ('PaperTodo-E004-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixtures | Out-Null
$rows = @()
function EventParts($Events, [string]$Name, [int]$Nth = 1) {
  $matches = @($Events | Where-Object { $_ -like "*|$Name|*" })
  if ($matches.Count -lt $Nth) { throw "missing event $Name #$Nth" }
  return ($matches[$Nth - 1] -split '\|')
}
try {
  foreach ($variant in @('baseline','candidate')) {
    $root = Join-Path $Workspace $variant
    python "$Workspace/.github/e004-app.py" $root
    if ($LASTEXITCODE -ne 0) { throw 'real app probe failed' }
    foreach ($format in @('multifile','compressed')) {
      $out = Join-Path $fixtures "publish-$variant-$format"
      $selfContained = if ($format -eq 'compressed') { 'true' } else { 'false' }
      dotnet publish "$root/PaperTodo.csproj" -c Release -r win-x64 --self-contained $selfContained -o $out "/p:PublishSingleFile=$selfContained" "/p:EnableCompressionInSingleFile=$selfContained" /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishReadyToRun=false /p:PublishTrimmed=false /p:EmbedAllSources=false /p:EmbedUntrackedSources=false /p:DebugType=none /p:DebugSymbols=false > "$evidence/$variant-$format-publish.log" 2>&1
      if ($LASTEXITCODE -ne 0) { Get-Content "$evidence/$variant-$format-publish.log" -Tail 60; throw 'publish failed' }
    }
  }
  foreach ($format in @('multifile','compressed')) {
    foreach ($mode in @('baseline','fresh','recorded')) {
      $source = if ($mode -eq 'baseline') { 'baseline' } else { 'candidate' }
      $dest = Join-Path $fixtures "$mode-$format"
      New-Item -ItemType Directory -Force $dest | Out-Null
      Copy-Item (Join-Path $fixtures "publish-$source-$format/*") $dest -Recurse
      $papers = @(0..9 | ForEach-Object {
        @{ id="fixture-$_"; type='note'; content="real-app note $_"; isVisible=$true; isCollapsed=$true; x=60; y=60; width=300; height=240; capsuleSide='right' }
      })
      @{ papers=$papers; telemetryEnabled=$false; enableAnimations=$true; useCapsuleMode=$true; useDeepCapsuleMode=$true; experimentalEdgeCapsuleHoverPreview=$true; usePersistentPowerShellProcess=$false; mcpEnabled=$false } |
        ConvertTo-Json -Depth 12 | Set-Content "$dest/data.json" -Encoding utf8
    }
  }
  foreach ($sampleIndex in 0..4) {
    $order = if ($sampleIndex % 2 -eq 0) { @('baseline','fresh','recorded') } else { @('recorded','fresh','baseline') }
    foreach ($format in @('multifile','compressed')) {
      foreach ($mode in $order) {
        $dest = Join-Path $fixtures "$mode-$format"
        $profile = Join-Path $fixtures "profile-$mode-$format"
        if ($mode -eq 'fresh' -and (Test-Path $profile)) { Remove-Item $profile -Recurse -Force }
        $profileBefore = if (Test-Path "$profile/startup.prof") { (Get-Item "$profile/startup.prof").Length } else { 0 }
        if ($mode -eq 'recorded' -and $sampleIndex -gt 0 -and $profileBefore -eq 0) { throw 'no recorded profile to test' }
        $log = Join-Path $evidence "$sampleIndex-$mode-$format-app.tsv"
        $env:E004_APP_LOG = $log
        $env:E004_APP_PROFILE = $profile
        $started = [Diagnostics.Stopwatch]::GetTimestamp()
        $primary = Start-Process "$dest/PaperTodo.exe" -WorkingDirectory $dest -PassThru
        $secondary = $null
        try {
          $ready = $null; $deadline = [DateTime]::UtcNow.AddSeconds(20)
          while (!$ready -and [DateTime]::UtcNow -lt $deadline -and !$primary.HasExited) {
            if (Test-Path $log) { $ready = Get-Content $log | Where-Object { $_ -like "$($primary.Id)|Ready|*" } | Select-Object -First 1 }
            if (!$ready) { Start-Sleep -Milliseconds 20 }
          }
          if (!$ready) { throw "$mode/$format did not reach command Ready" }
          Start-Sleep -Seconds 4
          $secondary = Start-Process "$dest/PaperTodo.exe" -WorkingDirectory $dest -ArgumentList '--exit' -PassThru
          if (!$primary.WaitForExit(15000)) { throw 'primary did not exit normally' }
          $ended = [Diagnostics.Stopwatch]::GetTimestamp()
          if (!$secondary.WaitForExit(10000)) { throw 'secondary did not exit' }
          if ($primary.ExitCode -ne 0 -or $secondary.ExitCode -ne 0) { throw 'nonzero exit code' }
          $allEvents = @(Get-Content $log)
          $events = @($allEvents | Where-Object { $_ -like "$($primary.Id)|*" })
          $request = EventParts $events 'ExitRequest'
          $render = EventParts $events 'Rendering'
          $cache = EventParts $events 'Cache' 10
          $shell = EventParts $events 'Shell' 10
          $metrics = EventParts $events 'IdleMetrics'
          $readyParts = EventParts $events 'Ready'
          if (@($events | Where-Object { $_ -like '*|OnExitComplete|*' }).Count -ne 1) { throw 'OnExit was skipped' }
          if (@($events | Where-Object { $_ -like '*|Cache|*' }).Count -ne 10) { throw 'cache was duplicated or incomplete' }
          $profilesStarted = @($allEvents | Where-Object { $_ -like '*|ProfileStarted|*' }).Count
          $expectedStarts = if ($mode -eq 'baseline') { 0 } else { 1 }
          if ($profilesStarted -ne $expectedStarts) { throw 'secondary process clobbered primary profile' }
          $state = Get-Content "$dest/data.json" -Raw | ConvertFrom-Json
          if ($state.papers.Count -ne 10 -or @($state.papers | Where-Object { !$_.isVisible }).Count -ne 0) { throw 'visibility or paper count changed' }
          foreach ($paper in $state.papers) {
            $i = [int]($paper.id -replace '^fixture-','')
            if ($paper.content -ne "real-app note $i") { throw 'note content changed' }
          }
          $profileAfter = if (Test-Path "$profile/startup.prof") { (Get-Item "$profile/startup.prof").Length } else { 0 }
          if ($mode -ne 'baseline' -and $profileAfter -eq 0) { throw 'runtime profile was not written' }
          $frequency = [long]$readyParts[3]
          $rows += [pscustomobject]@{ sampleIndex=$sampleIndex; mode=$mode; format=$format; renderingMs=1000.0*([long]$render[2]-$started)/$frequency; readyMs=1000.0*([long]$readyParts[2]-$started)/$frequency; cacheMs=1000.0*([long]$cache[2]-$started)/$frequency; shellMs=1000.0*([long]$shell[2]-$started)/$frequency; exitMs=1000.0*($ended-[long]$request[2])/$frequency; idleWorkingSet=[long]$metrics[2]; idlePrivateBytes=[long]$metrics[3]; cpuMs=[double]::Parse($metrics[4],[Globalization.CultureInfo]::InvariantCulture); profileBefore=$profileBefore; profileAfter=$profileAfter }
          $rows | Export-Csv "$evidence/real-app.csv" -NoTypeInformation
        }
        finally {
          if ($primary -and !$primary.HasExited) { $primary.Kill($true); $primary.WaitForExit() }
          if ($secondary -and !$secondary.HasExited) { $secondary.Kill($true); $secondary.WaitForExit() }
          if ($primary) { $primary.Dispose() }
          if ($secondary) { $secondary.Dispose() }
        }
      }
    }
  }
  $rows | Format-Table -AutoSize | Out-String -Width 240 | Write-Host
}
finally {
  Remove-Item Env:E004_APP_LOG -ErrorAction SilentlyContinue
  Remove-Item Env:E004_APP_PROFILE -ErrorAction SilentlyContinue
  Remove-Item $fixtures -Recurse -Force -ErrorAction SilentlyContinue
}
