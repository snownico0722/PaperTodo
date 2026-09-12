$ErrorActionPreference = 'Stop'
$root = $env:GITHUB_WORKSPACE
$out = Join-Path $root 'benchmark-output'
$pubRoot = Join-Path $env:RUNNER_TEMP 'papertodo-publish-variants'
Remove-Item $out, $pubRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out, $pubRoot | Out-Null

dotnet restore .\PaperTodo.csproj *> (Join-Path $out 'restore.log')
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

# First prove whether real, uninstrumented source can be published as ReadyToRun.
$realR2r = Join-Path $pubRoot 'real-source-r2r-probe'
New-Item -ItemType Directory -Force $realR2r | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()
& dotnet publish .\PaperTodo.csproj -c Release -r win-x64 --self-contained true -o $realR2r /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:PublishTrimmed=false /p:DebugType=none /p:DebugSymbols=false *> (Join-Path $out 'real-source-r2r-publish.log')
$realR2rExit = $LASTEXITCODE
$sw.Stop()
$realR2rResult = [ordered]@{ success = ($realR2rExit -eq 0); exitCode = $realR2rExit; publishMs = $sw.Elapsed.TotalMilliseconds }

@'
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace PaperTodo;

internal static class StartupBenchmarkProbe
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Marks = new(StringComparer.Ordinal);
    private static readonly string? OutputPath = Environment.GetEnvironmentVariable("PAPERTODO_STARTUP_PROBE");
    private static bool _scheduled;

    [ModuleInitializer]
    internal static void ModuleInit() => Mark("managed_module");

    internal static void Mark(string name)
    {
        if (string.IsNullOrWhiteSpace(OutputPath)) return;
        lock (Gate)
            if (!Marks.ContainsKey(name)) Marks[name] = Stopwatch.GetTimestamp();
    }

    internal static void ScheduleReady(AppController controller)
    {
        if (string.IsNullOrWhiteSpace(OutputPath) || _scheduled) return;
        _scheduled = true;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            var expected = controller.BenchmarkExpectedVisibleSurfaceCount;
            var visible = controller.BenchmarkVisibleSurfaceCount;
            if (visible > 0) Mark("first_render_with_surface");
            if (expected <= 0 || visible < expected) return;
            CompositionTarget.Rendering -= handler;
            Mark("all_surfaces_rendering");
            int hr;
            try { hr = DwmFlush(); } catch { hr = int.MinValue; }
            Mark("dwm_flush_complete");
            Dictionary<string, long> snapshot;
            lock (Gate) snapshot = new Dictionary<string, long>(Marks, StringComparer.Ordinal);
            var payload = new
            {
                phase = "ready",
                pid = Environment.ProcessId,
                frequency = Stopwatch.Frequency,
                expected,
                visible,
                dwmFlushHresult = hr,
                workingSet = Environment.WorkingSet,
                marks = snapshot
            };
            try { File.WriteAllText(OutputPath!, JsonSerializer.Serialize(payload)); } catch { }
        };
        CompositionTarget.Rendering += handler;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

public sealed partial class AppController
{
    internal int BenchmarkVisibleSurfaceCount => _windows.Values.Count(window => window.HasVisibleSurface);
    internal int BenchmarkExpectedVisibleSurfaceCount => State.Papers.Count(paper => paper.IsVisible && !_startupDisplayDeferredPapers.Contains(paper));
}
'@ | Set-Content -LiteralPath .\src\StartupBenchmarkProbe.cs -Encoding utf8

@'
from pathlib import Path

def repl(path, old, new):
    p = Path(path)
    text = p.read_text(encoding='utf-8')
    if text.count(old) != 1:
        raise SystemExit(f"expected one match in {path}: {old[:80]!r}, got {text.count(old)}")
    p.write_text(text.replace(old, new), encoding='utf-8', newline='\n')

repl('App.xaml.cs',
'''    protected override async void OnStartup(StartupEventArgs e)\n    {\n''',
'''    protected override async void OnStartup(StartupEventArgs e)\n    {\n        StartupBenchmarkProbe.Mark("app_onstartup");\n''')
repl('src/AppController.cs',
'''    public AppController()\n    {\n        Current = this;\n''',
'''    public AppController()\n    {\n        StartupBenchmarkProbe.Mark("controller_ctor_begin");\n        Current = this;\n''')
repl('src/AppController.cs',
'''        _paperBodyPlugins = new PaperBodyPluginRegistry();\n\n        // Idle debounce''',
'''        _paperBodyPlugins = new PaperBodyPluginRegistry();\n        StartupBenchmarkProbe.Mark("controller_ctor_end");\n\n        // Idle debounce''')
repl('src/AppController.cs',
'''    {\n        CreateTrayIcon();\n        InitializeGlobalHotkeys();\n''',
'''    {\n        StartupBenchmarkProbe.Mark("start_async_enter");\n        CreateTrayIcon();\n        InitializeGlobalHotkeys();\n''')
repl('src/AppController.cs',
'''        RefreshTrayMenu();\n        // Shell construction can invalidate preview resources. Queue every reader now, but\n''',
'''        RefreshTrayMenu();\n        StartupBenchmarkProbe.Mark("restore_surfaces_end");\n        StartupBenchmarkProbe.ScheduleReady(this);\n        // Shell construction can invalidate preview resources. Queue every reader now, but\n''')
'@ | python -
if ($LASTEXITCODE -ne 0) { throw 'instrumentation patch failed' }

$variants = @(
    @{ name='sc-single-compressed'; sc=$true; single=$true; r2r=$false; compression=$true },
    @{ name='sc-single-uncompressed'; sc=$true; single=$true; r2r=$false; compression=$false },
    @{ name='sc-single-r2r-compressed'; sc=$true; single=$true; r2r=$true; compression=$true },
    @{ name='sc-single-r2r-uncompressed'; sc=$true; single=$true; r2r=$true; compression=$false },
    @{ name='sc-multifile'; sc=$true; single=$false; r2r=$false; compression=$false },
    @{ name='sc-multifile-r2r'; sc=$true; single=$false; r2r=$true; compression=$false },
    @{ name='fd-single'; sc=$false; single=$true; r2r=$false; compression=$false },
    @{ name='fd-single-r2r'; sc=$false; single=$true; r2r=$true; compression=$false }
)

$publishResults = @()
foreach ($v in $variants) {
    $dir = Join-Path $pubRoot $v.name
    New-Item -ItemType Directory -Force $dir | Out-Null
    $log = Join-Path $out ("publish-{0}.log" -f $v.name)
    $args = @('publish','.\PaperTodo.csproj','-c','Release','-r','win-x64','-o',$dir,
        ("--self-contained={0}" -f $v.sc.ToString().ToLowerInvariant()),
        ("/p:PublishSingleFile={0}" -f $v.single.ToString().ToLowerInvariant()),
        ("/p:PublishReadyToRun={0}" -f $v.r2r.ToString().ToLowerInvariant()),
        '/p:IncludeNativeLibrariesForSelfExtract=true',
        ("/p:EnableCompressionInSingleFile={0}" -f $v.compression.ToString().ToLowerInvariant()),
        '/p:PublishTrimmed=false','/p:DebugType=none','/p:DebugSymbols=false')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @args *> $log
    $code = $LASTEXITCODE
    $timer.Stop()
    $size = if ($code -eq 0) { (Get-ChildItem $dir -File -Recurse | Measure-Object Length -Sum).Sum } else { 0 }
    $exe = Join-Path $dir 'PaperTodo.exe'
    $exeSize = if (Test-Path $exe) { (Get-Item $exe).Length } else { 0 }
    $publishResults += [pscustomobject]@{ variant=$v.name; success=($code -eq 0); exitCode=$code; publishMs=$timer.Elapsed.TotalMilliseconds; bytes=$size; exeBytes=$exeSize; selfContained=$v.sc; singleFile=$v.single; readyToRun=$v.r2r; compression=$v.compression }
}
$publishResults | Export-Csv (Join-Path $out 'publish-results.csv') -NoTypeInformation

function Write-FixtureState([string]$dir) {
    $papers = @()
    for ($i=0; $i -lt 10; $i++) {
        $papers += [ordered]@{ id="bench-$i"; type='note'; content="short benchmark note $i"; isVisible=$true; isCollapsed=$true; x=120.0; y=120.0; width=300.0; height=240.0; capsuleSide='right' }
    }
    $state = [ordered]@{ telemetryEnabled=$false; enableAnimations=$true; useCapsuleMode=$true; useDeepCapsuleMode=$true; experimentalEdgeCapsuleHoverPreview=$true; usePersistentPowerShellProcess=$false; mcpEnabled=$false; papers=$papers }
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $dir 'data.json') -Encoding utf8
}

function DeltaMs([long]$a, [long]$b, [long]$freq) { return (($b-$a) * 1000.0 / $freq) }

$samples = @()
foreach ($p in $publishResults | Where-Object success) {
    $source = Join-Path $pubRoot $p.variant
    for ($pair=1; $pair -le 3; $pair++) {
        $extractBase = Join-Path $env:RUNNER_TEMP ("bundle-{0}-{1}" -f $p.variant,$pair)
        Remove-Item $extractBase -Recurse -Force -ErrorAction SilentlyContinue
        foreach ($mode in @('fresh','warm')) {
            $runDir = Join-Path $env:RUNNER_TEMP ("run-{0}-{1}-{2}-{3}" -f $p.variant,$pair,$mode,[guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force $runDir | Out-Null
            Copy-Item (Join-Path $source '*') $runDir -Recurse -Force
            Write-FixtureState $runDir
            $marker = Join-Path $runDir 'startup-probe.json'
            $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $runDir 'PaperTodo.exe'))
            $psi.WorkingDirectory = $runDir
            $psi.UseShellExecute = $false
            $psi.Environment['PAPERTODO_STARTUP_PROBE'] = $marker
            $psi.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractBase
            $t0 = [Diagnostics.Stopwatch]::GetTimestamp()
            $proc = [Diagnostics.Process]::Start($psi)
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            while (-not (Test-Path $marker)) {
                if ($proc.HasExited) { throw "$($p.variant) $mode exited before ready: $($proc.ExitCode)" }
                if ([DateTime]::UtcNow -gt $deadline) { try { $proc.Kill($true) } catch {}; throw "$($p.variant) $mode ready timeout" }
                Start-Sleep -Milliseconds 10
            }
            $readySeen = [Diagnostics.Stopwatch]::GetTimestamp()
            $payload = Get-Content $marker -Raw | ConvertFrom-Json
            $freq = [long]$payload.frequency
            $m = $payload.marks
            $exitPsi = [Diagnostics.ProcessStartInfo]::new((Join-Path $runDir 'PaperTodo.exe'))
            $exitPsi.WorkingDirectory = $runDir
            $exitPsi.UseShellExecute = $false
            $exitPsi.ArgumentList.Add('--exit')
            $exitPsi.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $extractBase
            $exitT0 = [Diagnostics.Stopwatch]::GetTimestamp()
            $secondary = [Diagnostics.Process]::Start($exitPsi)
            $secondary.WaitForExit(10000) | Out-Null
            if (-not $proc.WaitForExit(15000)) { try { $proc.Kill($true) } catch {}; throw "$($p.variant) exit timeout" }
            $exitDone = [Diagnostics.Stopwatch]::GetTimestamp()
            $extractBytes = if (Test-Path $extractBase) { (Get-ChildItem $extractBase -File -Recurse | Measure-Object Length -Sum).Sum } else { 0 }
            $extractFiles = if (Test-Path $extractBase) { @(Get-ChildItem $extractBase -File -Recurse).Count } else { 0 }
            $samples += [pscustomobject]@{
                variant=$p.variant; pair=$pair; mode=$mode; processExitCode=$proc.ExitCode; dwmHresult=$payload.dwmFlushHresult;
                managedEntryMs=(DeltaMs $t0 ([long]$m.managed_module) $freq);
                appOnStartupMs=(DeltaMs $t0 ([long]$m.app_onstartup) $freq);
                controllerCtorBeginMs=(DeltaMs $t0 ([long]$m.controller_ctor_begin) $freq);
                controllerCtorEndMs=(DeltaMs $t0 ([long]$m.controller_ctor_end) $freq);
                startAsyncEnterMs=(DeltaMs $t0 ([long]$m.start_async_enter) $freq);
                restoreSurfacesEndMs=(DeltaMs $t0 ([long]$m.restore_surfaces_end) $freq);
                firstRenderSurfaceMs=(DeltaMs $t0 ([long]$m.first_render_with_surface) $freq);
                allSurfacesRenderingMs=(DeltaMs $t0 ([long]$m.all_surfaces_rendering) $freq);
                dwmFlushCompleteMs=(DeltaMs $t0 ([long]$m.dwm_flush_complete) $freq);
                parentSawReadyMs=(DeltaMs $t0 $readySeen $freq);
                exitMs=(DeltaMs $exitT0 $exitDone $freq);
                workingSet=[long]$payload.workingSet; extractBytes=[long]$extractBytes; extractFiles=$extractFiles
            }
            Remove-Item $runDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
$samples | Export-Csv (Join-Path $out 'startup-samples.csv') -NoTypeInformation

$summary = @()
foreach ($v in $publishResults) {
    $rows = @($samples | Where-Object variant -eq $v.variant)
    $item = [ordered]@{ variant=$v.variant; publishSuccess=$v.success; publishMs=$v.publishMs; bytes=$v.bytes; exeBytes=$v.exeBytes; selfContained=$v.selfContained; singleFile=$v.singleFile; readyToRun=$v.readyToRun; compression=$v.compression }
    foreach ($mode in @('fresh','warm')) {
        $set = @($rows | Where-Object mode -eq $mode)
        if ($set.Count -gt 0) {
            foreach ($metric in @('managedEntryMs','appOnStartupMs','controllerCtorEndMs','restoreSurfacesEndMs','dwmFlushCompleteMs','exitMs','workingSet','extractBytes')) {
                $vals = @($set | ForEach-Object { [double]($_.$metric) } | Sort-Object)
                $item["$mode-$metric-median"] = $vals[[int][math]::Floor($vals.Count/2)]
            }
        }
    }
    $summary += [pscustomobject]$item
}
$summary | Export-Csv (Join-Path $out 'summary.csv') -NoTypeInformation
[ordered]@{ realSourceR2R=$realR2rResult; variants=$publishResults; samples=$samples } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $out 'raw.json') -Encoding utf8

Write-Host '=== SUMMARY ==='
$summary | Format-Table variant,publishSuccess,exeBytes,'fresh-managedEntryMs-median','fresh-dwmFlushCompleteMs-median','warm-dwmFlushCompleteMs-median','fresh-exitMs-median','fresh-extractBytes-median' -AutoSize
if (-not ($publishResults | Where-Object variant -eq 'sc-single-compressed').success) { throw 'release baseline publish failed' }
