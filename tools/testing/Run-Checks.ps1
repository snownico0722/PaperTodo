#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('regression', 'plugins', 'persistence', 'diagnostics', 'visual', 'all')]
    [string] $Group = 'regression',
    [ValidateNotNullOrEmpty()]
    [string[]] $Project = @(),
    [switch] $List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# One-off acceptance on isolated Windows WPF processes.
if ($env:GITHUB_REF_NAME -eq 'diagnostics/issue-456-fix-verification' -and $Group -eq 'regression' -and !$List) {
    $projectPath = Join-Path $repository 'tests/PaperTodo.LifecycleChecks/PaperTodo.LifecycleChecks.csproj'
    $productCommit = 'c41c2593b27e6f3838f182d67c1c25bcc3422628'
    foreach ($path in @('src/MasterCapsuleWindow.cs','src/EdgeCapsuleDragWindow.cs','tests/PaperTodo.LifecycleChecks/MasterQueueDropHandoffChecks.cs')) {
        if ((git hash-object $path) -ne (git rev-parse ("${productCommit}:$path"))) { throw "Different candidate file: $path" }
    }
    dotnet build $projectPath -c Release
    if ($LASTEXITCODE) { throw 'Cannot build candidate' }
    $failures = @()
    $cases = @('master-queue-drop-handoff','master-queue-drag-preparation','held-drag-preparation',
        'issue456-direct','issue456-classic','issue456-free','issue456-forced','issue456-direct-forced',
        'issue456-resize','issue456-layout-resize','issue456-layout-resize','issue456-layout-resize')
    foreach ($trial in $cases) {
        $case = $trial
        $env:ISSUE456_HOLD = '0'
        if ($trial -eq 'held-drag-preparation') { $case = 'master-queue-drag-preparation'; $env:ISSUE456_HOLD = '1' }
        $lines = @(& dotnet run --no-build --project $projectPath -c Release -- --case $case 2>&1)
        $code = $LASTEXITCODE
        $log = ($lines | Out-String -Width 1600)
        Write-Host $log
        $ok = $code -eq 0 -and !$log.Contains('DISPATCHER_UNHANDLED') -and !$log.Contains('MATCHING_FIRST_CHANCE')
        if ($trial -eq 'issue456-layout-resize') { $ok = $ok -and $log -match 'RESIZE_ENDPOINT reached=\w+ depth=[1-9]\d* insideMeasure=True insideHwndResize=True' }
        if ($trial -eq 'issue456-resize') { $ok = $ok -and $log -match 'RESIZE_ENDPOINT reached=\w+ depth=0 insideMeasure=False insideHwndResize=True' }
        if ($trial.EndsWith('-forced')) { $ok = $ok -and $log.Contains('INJECT_DISABLE_PROCESSING') }
        if ($trial -eq 'issue456-direct') { $ok = $ok -and $log.Contains('PREPARE_RETURN earlierRenderRan=True') }
        if ($trial -eq 'held-drag-preparation') { $ok = $ok -and $log.Contains('PREP_BEFORE received=True leftPressed=True') -and $log.Contains('PREP_AFTER held=True prepared=True nativeDragStarted=False sameMembers=True') }
        Write-Host "ACCEPT case=$trial exit=$code pass=$ok"
        if (!$ok) { $failures += $trial }
    }
    if ($failures.Count) { throw "Failed cases: $($failures -join ', ')" }
    exit 0
}

# This is the shared local/CI inventory. Keep configuration differences explicit;
# Debug collector checks and Release no-op checks protect different behavior.
$checks = @(
    @{ Group = 'regression'; Project = 'WindowStackChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'MarkdownSemanticChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'MarkdownEditingChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'TodoNavigationChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'EdgeTitleChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'EdgePreviewChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'ThreadingChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'LifecycleChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'WindowCloseActivationChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'plugins'; Project = 'ProtocolPolicyChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'plugins'; Project = 'PluginApiChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'plugins'; Project = 'SettingsApiChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'plugins'; Project = 'CodexCliBridgeChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'persistence'; Project = 'PersistenceChecks'; Configuration = 'Release'; Runtime = 'win-x64' }
    @{ Group = 'persistence'; Project = 'DataReloadChecks'; Configuration = 'Release'; Runtime = 'win-x64' }
    @{ Group = 'diagnostics'; Project = 'EdgeDiagnosticJournalChecks'; Configuration = 'Debug'; Runtime = '' }
    @{ Group = 'diagnostics'; Project = 'EdgeDiagnosticJournalChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'diagnostics'; Project = 'EdgeLatencyObservationChecks'; Configuration = 'Debug'; Runtime = '' }
    @{ Group = 'diagnostics'; Project = 'EdgeTitleChecks'; Configuration = 'Debug'; Runtime = '' }
    @{ Group = 'visual'; Project = 'MicaChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'plugins'; Project = 'plugin-samples'; Configuration = ''; Runtime = '' }
)
$selected = @($checks | Where-Object { $Group -eq 'all' -or $_.Group -eq $Group })
if ($Project.Count) {
    $unknown = @($Project | Where-Object { $_ -notin $selected.Project })
    if ($unknown.Count) { throw "Projects not in group '${Group}': $($unknown -join ', '). Use -Group all -List to inspect the inventory." }
    $selected = @($selected | Where-Object { $_.Project -in $Project })
}
if (-not $selected.Count) { throw 'No checks selected.' }
if ($List) {
    $selected | ForEach-Object { [pscustomobject] $_ } | Format-Table Group, Project, Configuration, Runtime
    return
}
if (-not $IsWindows) { throw 'These checks require Windows. Use -List to inspect the groups without running them.' }
$null = Get-Command dotnet -ErrorAction Stop
if ($selected.Group -contains 'plugins') { $null = Get-Command node -ErrorAction Stop }

$results = [System.Collections.Generic.List[object]]::new()
Push-Location $repository
try {
    foreach ($check in $selected) {
        $name = if ($check.Configuration) { "PaperTodo.$($check.Project) [$($check.Configuration)]" } else { $check.Project }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $passed = $false
        Write-Host "`n=== $name ==="
        try {
            if ($check.Project -eq 'plugin-samples') {
                & ./tests/plugin-samples/Check-Samples.ps1
            }
            else {
                $projectPath = "tests/PaperTodo.$($check.Project)/PaperTodo.$($check.Project).csproj"
                $arguments = @('run', '--project', $projectPath, '-c', $check.Configuration)
                if ($check.Runtime) { $arguments += @('-r', $check.Runtime) }
                & dotnet @arguments
                if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
            }
            $passed = $true
        }
        catch { Write-Host "FAIL ${name}: $($_.Exception.Message)" }
        finally {
            $clock.Stop()
            $results.Add([pscustomobject]@{ Check = $name; Passed = $passed; Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2) })
        }
    }
}
finally { Pop-Location }

$results | Format-Table -AutoSize | Out-Host
$failed = @($results | Where-Object { -not $_.Passed }).Count
if ($failed) { throw "$failed check group(s) failed. See the original errors above." }
Write-Host "All $($results.Count) selected check groups passed."
