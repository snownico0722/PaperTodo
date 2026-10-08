#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('regression', 'plugins', 'persistence', 'diagnostics', 'all')]
    [string] $Group = 'regression',
    [switch] $List
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# Keep configurations explicit: a Release test of the Journal is not a Debug collector test.
$checks = @(
    @{ Group = 'regression'; Project = 'WindowStackChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'MarkdownSemanticChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'MarkdownMathChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'regression'; Project = 'MarkdownMathLayoutChecks'; Configuration = 'Release'; Runtime = '' }
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
    @{ Group = 'diagnostics'; Project = 'EdgeDiagnosticJournalChecks'; Configuration = 'Debug'; Runtime = '' }
    @{ Group = 'diagnostics'; Project = 'EdgeDiagnosticJournalChecks'; Configuration = 'Release'; Runtime = '' }
    @{ Group = 'diagnostics'; Project = 'EdgeLatencyObservationChecks'; Configuration = 'Debug'; Runtime = '' }
    @{ Group = 'diagnostics'; Project = 'EdgeTitleChecks'; Configuration = 'Debug'; Runtime = '' }
)
$selected = @($checks | Where-Object { $Group -eq 'all' -or $_.Group -eq $Group })
if ($List) {
    $selected | ForEach-Object { [pscustomobject] $_ } | Format-Table Group, Project, Configuration, Runtime
    if ($Group -in @('plugins', 'all')) { Write-Host 'Also runs tests/plugin-samples/Check-Samples.ps1.' }
    return
}
if (-not $IsWindows) { throw 'These checks require Windows. Use -List to inspect the groups without running them.' }
$null = Get-Command dotnet -ErrorAction Stop
if ($Group -in @('plugins', 'all')) { $null = Get-Command node -ErrorAction Stop }

$results = [System.Collections.Generic.List[object]]::new()
Push-Location $repository
try {
    foreach ($check in $selected) {
        $name = "PaperTodo.$($check.Project) [$($check.Configuration)]"
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $passed = $false
        Write-Host "`n=== $name ==="
        try {
            $project = "tests/PaperTodo.$($check.Project)/PaperTodo.$($check.Project).csproj"
            $arguments = @('run', '--project', $project, '-c', $check.Configuration)
            if ($check.Runtime) { $arguments += @('-r', $check.Runtime) }
            & dotnet @arguments
            if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
            $passed = $true
        }
        catch { Write-Host "FAIL ${name}: $($_.Exception.Message)" }
        finally {
            $clock.Stop()
            $results.Add([pscustomobject]@{ Check = $name; Passed = $passed; Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2) })
        }
    }
    if ($Group -in @('plugins', 'all')) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $passed = $false
        try {
            & ./tests/plugin-samples/Check-Samples.ps1
            $passed = $true
        }
        catch { Write-Host "FAIL plugin samples: $($_.Exception.Message)" }
        finally {
            $clock.Stop()
            $results.Add([pscustomobject]@{ Check = 'Plugin sample contracts and artifacts'; Passed = $passed; Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2) })
        }
    }
}
finally { Pop-Location }

$results | Format-Table -AutoSize | Out-Host
$failed = @($results | Where-Object { -not $_.Passed }).Count
if ($failed) { throw "$failed check group(s) failed. See the original errors above." }
Write-Host "All $($results.Count) selected check groups passed."
