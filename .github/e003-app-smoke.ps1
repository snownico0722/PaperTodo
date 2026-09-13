param([string]$Workspace = '.')
$ErrorActionPreference = 'Stop'
$Workspace = (Resolve-Path $Workspace).Path
$evidence = Join-Path $Workspace 'evidence'
$fixtures = Join-Path $env:RUNNER_TEMP ('PaperTodo-E003-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixtures | Out-Null
$rows = @()
try {
  foreach ($variant in @('baseline','target')) {
    $root = Join-Path $Workspace $variant
    python "$Workspace/tools/.github/e003-app-smoke.py" $root instrument
    if ($LASTEXITCODE -ne 0) { throw 'smoke instrumentation failed' }
    try {
      dotnet build "$root/PaperTodo.csproj" -c Release 2>&1 | Tee-Object "$evidence/$variant-app-build.log"
      if ($LASTEXITCODE -ne 0) { throw "$variant app build failed" }
      $exe = Get-ChildItem "$root/输出" -Recurse -Filter PaperTodo.exe | Where-Object FullName -NotMatch '\\publish\\' | Select-Object -First 1
      if (!$exe) { throw 'app executable missing' }
      $dest = Join-Path $fixtures $variant
      New-Item -ItemType Directory -Force $dest | Out-Null
      foreach ($file in Get-ChildItem $exe.DirectoryName -File) {
        if ($file.Extension -in @('.exe','.dll','.pdb') -or $file.Name -like '*.deps.json' -or $file.Name -like '*.runtimeconfig.json') {
          Copy-Item $file.FullName $dest
        }
      }
      foreach ($locale in @('en','ja','ko','runtimes')) {
        if (Test-Path (Join-Path $exe.DirectoryName $locale)) { Copy-Item (Join-Path $exe.DirectoryName $locale) $dest -Recurse }
      }
      $papers = @(0..9 | ForEach-Object {
        @{ id="fixture-$_"; type='note'; content="real-app note $_"; isVisible=$true; isCollapsed=$true; x=60; y=60; width=300; height=240; capsuleSide='right' }
      })
      @{ papers=$papers; telemetryEnabled=$false; enableAnimations=$true; useCapsuleMode=$true; useDeepCapsuleMode=$true; experimentalEdgeCapsuleHoverPreview=$true; usePersistentPowerShellProcess=$false; mcpEnabled=$false } |
        ConvertTo-Json -Depth 12 | Set-Content "$dest/data.json" -Encoding utf8
    }
    finally {
      python "$Workspace/tools/.github/e003-app-smoke.py" $root restore
      if ($LASTEXITCODE -ne 0) { throw 'smoke source restore failed' }
    }
  }
  foreach ($round in 0..3) {
    $order = if ($round % 2 -eq 0) { @('baseline','target') } else { @('target','baseline') }
    foreach ($variant in $order) {
      $dest = Join-Path $fixtures $variant
      $log = Join-Path $evidence "$round-$variant-real-app.tsv"
      $env:PAPERTODO_E003_SMOKE = $log
      $started = [Diagnostics.Stopwatch]::GetTimestamp()
      $primary = Start-Process "$dest/PaperTodo.exe" -WorkingDirectory $dest -PassThru
      $secondary = $null
      try {
        $ready = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (!$ready -and [DateTime]::UtcNow -lt $deadline -and !$primary.HasExited) {
          if (Test-Path $log) { $ready = Get-Content $log | Where-Object { $_ -like "$($primary.Id)|Ready|*" } | Select-Object -First 1 }
          if (!$ready) { Start-Sleep -Milliseconds 20 }
        }
        if (!$ready) { throw "$variant actual App did not reach Ready" }
        # Exercise telemetry attachment/OnExit after its 3-second bootstrap, not only the early-exit path.
        Start-Sleep -Seconds 4
        $secondary = Start-Process "$dest/PaperTodo.exe" -WorkingDirectory $dest -ArgumentList '--exit' -PassThru
        if (!$primary.WaitForExit(15000)) { throw "$variant main process did not exit" }
        $ended = [Diagnostics.Stopwatch]::GetTimestamp()
        if (!$secondary.WaitForExit(10000)) { throw 'secondary exit command hung' }
        if ($primary.ExitCode -ne 0 -or $secondary.ExitCode -ne 0) { throw 'nonzero real-app exit code' }
        $events = @(Get-Content $log | Where-Object { $_ -like "$($primary.Id)|*" })
        $request = ($events | Where-Object { $_ -like '*|ExitRequest|*' } | Select-Object -First 1) -split '\|'
        if ($request.Count -ne 4) { throw 'primary Exit request marker missing' }
        $onExit = @($events | Where-Object { $_ -like '*|OnExitComplete|*' }).Count -eq 1
        if ($variant -eq 'target' -and !$onExit) { throw 'real App.OnExit did not finish' }
        $state = Get-Content "$dest/data.json" -Raw | ConvertFrom-Json
        if ($state.papers.Count -ne 10 -or @($state.papers | Where-Object { !$_.isVisible }).Count -ne 0) { throw 'real App changed persistent visibility' }
        foreach ($paper in $state.papers) {
          $i = [int]($paper.id -replace '^fixture-','')
          if ($paper.content -ne "real-app note $i") { throw 'real App data changed unexpectedly' }
        }
        $readyParts = $ready -split '\|'
        $rows += [pscustomobject]@{ round=$round; variant=$variant; startupToReadyMs=1000.0*([long]$readyParts[2]-$started)/[long]$readyParts[3]; primaryExitMs=1000.0*($ended-[long]$request[2])/[long]$request[3]; onExitCompleted=$onExit; papers=$state.papers.Count }
        # Every next round restarts the same directory/instance names, validating mutex release and retained state.
        $rows | Export-Csv "$evidence/real-app-summary.csv" -NoTypeInformation
      }
      finally {
        if ($primary -and !$primary.HasExited) { $primary.Kill($true); $primary.WaitForExit() }
        if ($secondary -and !$secondary.HasExited) { $secondary.Kill($true); $secondary.WaitForExit() }
        if ($primary) { $primary.Dispose() }
        if ($secondary) { $secondary.Dispose() }
      }
    }
  }
  $rows | Format-Table -AutoSize | Out-String | Write-Host
}
finally {
  Remove-Item Env:PAPERTODO_E003_SMOKE -ErrorAction SilentlyContinue
  Remove-Item $fixtures -Recurse -Force -ErrorAction SilentlyContinue
}
