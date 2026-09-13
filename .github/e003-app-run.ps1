param([string]$Binaries = 'published', [string]$Evidence = 'evidence')
$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$Evidence = Join-Path $root $Evidence
New-Item -ItemType Directory -Force $Evidence | Out-Null
$rows = [System.Collections.Generic.List[object]]::new()
$frequency = [System.Diagnostics.Stopwatch]::Frequency
$modes = @('baseline','ordered','graceful')
function Measure-App([string]$mode, [int]$round) {
  $id = [guid]::NewGuid().ToString('N')
  $dir = Join-Path ([IO.Path]::GetTempPath()) "PaperTodo-E003-$id"
  New-Item -ItemType Directory $dir | Out-Null
  Copy-Item "$root/$Binaries/$mode/*" $dir -Recurse -Force
  $papers = @(for ($i=0;$i -lt 10;$i++) {
    @{ id="fixture-$i"; type='note'; content="short note $i"; isVisible=$true; isCollapsed=$true; x=60; y=60; width=300; height=240; capsuleSide='right' }
  })
  $state = @{ telemetryEnabled=$false; enableAnimations=$true; useCapsuleMode=$true; useDeepCapsuleMode=$true; experimentalEdgeCapsuleHoverPreview=$true; usePersistentPowerShellProcess=$false; mcpEnabled=$false; papers=$papers }
  $state | ConvertTo-Json -Depth 10 | Set-Content "$dir/data.json" -Encoding utf8
  $events = Join-Path $Evidence "app-$mode-$round.csv"
  $ready = "$dir/probe-ready"
  $psi = [Diagnostics.ProcessStartInfo]::new("$dir/PaperTodo.exe")
  $psi.UseShellExecute = $false
  $psi.WorkingDirectory = $dir
  $psi.Environment['E003_RUN_ID'] = $id
  $psi.Environment['E003_EVENTS'] = $events
  $psi.Environment['E003_READY'] = $ready
  $psi.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = "$dir/bundle"
  $start = [Diagnostics.Stopwatch]::GetTimestamp()
  $process = [Diagnostics.Process]::Start($psi)
  try {
    while (!(Test-Path $ready)) {
      if ($process.HasExited -or ([Diagnostics.Stopwatch]::GetTimestamp()-$start)/$frequency -gt 20) { throw "$mode did not reach measured ready boundaries" }
      Start-Sleep -Milliseconds 5
    }
    $exitSent = [Diagnostics.Stopwatch]::GetTimestamp()
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.',"E003-Pipe-$id",[IO.Pipes.PipeDirection]::Out)
    try {
      $pipe.Connect(5000)
      $writer = [IO.StreamWriter]::new($pipe)
      $writer.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('["--exit"]')))
      $writer.Flush()
      $writer.Dispose()
    } finally { $pipe.Dispose() }
    if (!$process.WaitForExit(10000)) { throw "$mode did not exit" }
    $end = [Diagnostics.Stopwatch]::GetTimestamp()
    if ($process.ExitCode -ne 0) { throw "$mode exit code $($process.ExitCode)" }
    $saved = Get-Content "$dir/data.json" -Raw | ConvertFrom-Json
    if ($saved.papers.Count -ne 10 -or @($saved.papers | Where-Object { !$_.isVisible }).Count) { throw 'exit lost papers or visibility' }
    $marks = @{}
    foreach ($line in Import-Csv $events) { $marks[$line.stage] = [long]$line.ticks }
    $row = [ordered]@{ mode=$mode; round=$round; startTicks=$start; endTicks=$end }
    foreach ($key in @('module','app_startup','controller_ready','capsules_rendering','capsules_dwm_boundary','startup_return','artifacts_ready','shells_ready','composition_ready','tray_ready')) {
      if (!$marks.ContainsKey($key)) { throw "missing marker $key" }
      $row[$key+'Ms'] = ($marks[$key]-$start)*1000.0/$frequency
    }
    foreach ($key in @('exit_saved','exit_ui_hidden','exit_cleanup_done')) { $row[$key+'Ms'] = ($marks[$key]-$marks.exit_enter)*1000.0/$frequency }
    $row['exitReceivedToProcessEndMs'] = ($end-$marks.exit_enter)*1000.0/$frequency
    $row['pipeSendToProcessEndMs'] = ($end-$exitSent)*1000.0/$frequency
    $row['appOnExitObserved'] = $marks.ContainsKey('app_on_exit')
    $row['workingSetBytes'] = $marks.ready_working_set_bytes
    $row['privateBytes'] = $marks.ready_private_bytes
    $rows.Add([pscustomobject]$row)
    $rows | Export-Csv "$Evidence/app-summary.csv" -NoTypeInformation
    $row | ConvertTo-Json -Compress | Write-Host
  } finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    if (Test-Path "$dir/crash.log") { Copy-Item "$dir/crash.log" "$Evidence/crash-$mode-$round.log" }
    $process.Dispose()
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
  }
}
foreach ($mode in $modes) { Measure-App $mode -1 }
for ($round=0;$round -lt 6;$round++) {
  for ($index=0;$index -lt $modes.Count;$index++) { Measure-App $modes[($index+$round)%$modes.Count] $round }
}
