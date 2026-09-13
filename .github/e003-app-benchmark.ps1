$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$output = Join-Path $root 'evidence'
$rows = @()
foreach ($round in 0..5) {
    $variants = @('baseline','overlap','delayed','combined')
    if ($round % 2 -eq 1) { [array]::Reverse($variants) }
    foreach ($variant in $variants) {
        $dir = Join-Path $output "$variant-app-$round"
        New-Item -ItemType Directory -Force $dir | Out-Null
        $appDir = Join-Path $dir 'app'
        New-Item -ItemType Directory -Force $appDir | Out-Null
        Copy-Item "variants/$variant/published/*" $appDir -Recurse -Force
        $papers = @(0..9 | ForEach-Object { @{
            id = "app-fixture-$_"; type = 'note'; content = "short note $_";
            isVisible = $true; isCollapsed = $true; x = 60; y = 60;
            width = 300; height = 240; capsuleSide = 'right'
        } })
        @{
            papers = $papers; telemetryEnabled = $false; enableAnimations = $true;
            useCapsuleMode = $true; useDeepCapsuleMode = $true;
            experimentalEdgeCapsuleHoverPreview = $true;
            usePersistentPowerShellProcess = $false; mcpEnabled = $false
        } | ConvertTo-Json -Depth 8 | Set-Content "$appDir/data.json" -Encoding utf8
        $env:PAPERTODO_E003_DIR = $dir
        $info = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $appDir 'PaperTodo.exe'))
        $info.UseShellExecute = $false
        $info.WorkingDirectory = $appDir
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $info
        $t0 = [System.Diagnostics.Stopwatch]::GetTimestamp()
        try {
            if (-not $process.Start()) { throw 'Process failed to start' }
            $wait = [System.Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-Path "$dir/ready")) {
                if ($process.HasExited) { throw "$variant exited before presentation" }
                if ($wait.Elapsed.TotalSeconds -gt 20) { throw "$variant presentation timeout" }
                Start-Sleep -Milliseconds 5
            }
            # Allow all speculative work to complete before measuring normal exit.
            Start-Sleep -Milliseconds 1800
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'PaperTodo-SingleInstance-Activate', [System.IO.Pipes.PipeDirection]::Out)
            try {
                $exitAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
                $pipe.Connect(2000)
                $writer = [System.IO.StreamWriter]::new($pipe)
                try {
                    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('["--exit"]'))
                    $writer.WriteLine($payload)
                    $writer.Flush()
                }
                finally { $writer.Dispose() }
            }
            finally { $pipe.Dispose() }
            if (-not $process.WaitForExit(10000)) { throw "$variant exit timeout" }
            $ended = [System.Diagnostics.Stopwatch]::GetTimestamp()
            if ($process.ExitCode -ne 0) { throw "$variant returned $($process.ExitCode)" }
            $events = @(Import-Csv "$dir/app-trace.tsv" -Delimiter "`t")
            function Stamp($name, $last = $false) {
                $items = @($events | Where-Object name -eq $name)
                if ($items.Count -eq 0) { return $null }
                $item = if ($last) { $items[-1] } else { $items[0] }
                return ([double]$item.tick - $t0) * 1000 / [double]$item.frequency
            }
            $saved = Get-Content "$appDir/data.json" -Raw | ConvertFrom-Json
            if ($saved.papers.Count -ne 10 -or @($saved.papers | Where-Object { -not $_.isVisible -or -not $_.isCollapsed }).Count -gt 0) {
                throw 'Exit changed persisted visibility or note count'
            }
            if (@($events | Where-Object name -eq 'shell-built').Count -ne 10) { throw 'Shell warmup incomplete' }
            if ($null -eq (Stamp 'artifacts-full')) { throw 'Artifact warmup incomplete' }
            $rows += [pscustomobject]@{
                variant = $variant; round = $round; warmup = ($round -eq 0);
                managedMs = (Stamp 'managed'); controllerMs = (Stamp 'controller-ready');
                renderMs = (Stamp 'visible-render-callback'); dwmProxyMs = (Stamp 'dwm-proxy');
                restoreMs = (Stamp 'restore-return'); startupReturnedMs = (Stamp 'app-startup-complete');
                shellsReadyMs = (Stamp 'shell-built' $true); artifactsReadyMs = (Stamp 'artifacts-full');
                exitMs = ($ended - $exitAt) * 1000 / [System.Diagnostics.Stopwatch]::Frequency;
                exitRequestMs = (Stamp 'exit-request'); uiHiddenMs = (Stamp 'ui-hidden');
                appExitObserved = ($null -ne (Stamp 'app-exit'));
                dispatcherFinishedObserved = ($null -ne (Stamp 'dispatcher-finished'))
            }
            $rows | Export-Csv "$output/actual-app.csv" -NoTypeInformation
            Write-Host ($rows[-1] | ConvertTo-Json -Compress)
        }
        finally {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
            Remove-Item $appDir -Recurse -Force
        }
    }
}
