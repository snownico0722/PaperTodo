from pathlib import Path
import os
import subprocess

# Compare the same reviewed baseline, not a mixture of successively edited candidates.
base = '9e176abfe33cbc0ca873c03576691b153f1299a0'
for path in ['src/AppController.cs', 'src/AppController.PluginStartup.cs',
             'src/PaperWindow.PluginBodies.cs', 'src/PaperWindow.PluginMiniView.cs',
             'src/AnimationHelper.cs', 'tests/PaperTodo.LifecycleChecks/Program.cs']:
    text = subprocess.check_output(['git', 'show', base + ':' + path]).decode('utf-8-sig')
    Path(path).write_text(text, encoding='utf-8', newline='\n')
import interaction_lifetime_review
from interaction_lifetime_review import edit

edit('tests/PaperTodo.LifecycleChecks/Program.cs', '"real-exit", "early-expand"',
     '"startup-intent-hide-all", "startup-intent-hide-one", "startup-intent-form", "startup-intent-delete", "startup-intent-unchanged", "startup-intent-other-paper", "startup-intent-before-schedule", "real-exit", "early-expand"')
edit('tests/PaperTodo.LifecycleChecks/Program.cs', '''            if (name == "missing-monitor")
''', '''            if (name.StartsWith("startup-intent-", StringComparison.Ordinal))
            {
                await StartupDeferredIntentChecks.Run(name, controller, windows.Values.First());
                return;
            }
            if (name == "missing-monitor")
''')

# Returning the existing async operation lets the regression await an actual completed request;
# this mechanical signature adaptation is identical in both sides of the comparison.
edit('src/AppController.PluginStartup.cs',
     '    private async void SchedulePluginStartupPapers(StartupCommandKind visibilityCommand)',
     '    private async Task SchedulePluginStartupPapers(StartupCommandKind visibilityCommand, int restoreGeneration)')
p = Path('src/AppController.cs')
s = p.read_text(encoding='utf-8-sig').replace('SchedulePluginStartupPapers(initialVisibilityCommand);',
    '_ = SchedulePluginStartupPapers(initialVisibilityCommand, _paperSurfaceRestoreGeneration);')
p.write_text(s, encoding='utf-8', newline='\n')

p = Path('tests/PaperTodo.LifecycleChecks/InteractionLifetimeChecks.cs')
s = p.read_text(encoding='utf-8-sig')
s = s.replace('AnimationHelper.FlashHighlight(border, Colors.Red, 50);',
              'AnimationHelper.FlashHighlight(border, Colors.Red, 250);')
needle = '            AnimationHelper.FlashHighlight(border, Colors.Yellow, 80);'
assert needle in s
s = s.replace(needle, '''            var visibleFlash = Stopwatch.StartNew();
            while (border.Background is not SolidColorBrush flash || flash.Color.A < 12 ||
                   flash.Color.R < 180 || flash.Color.G > 40)
            {
                if (visibleFlash.Elapsed > TimeSpan.FromSeconds(3))
                    throw new InvalidOperationException("FLASH_NOT_VISIBLE: base-safe animation did not display its highlight");
                await Task.Delay(5);
            }
''' + needle)
p.write_text(s, encoding='utf-8', newline='\n')

if os.environ.get('REVIEW_FIXED') != 'true':
    raise SystemExit(0)

edit('src/AppController.PluginStartup.cs', '''        try
        {
            // Even''', '''        var requests = candidates.ToDictionary(descriptor => descriptor.Id, descriptor =>
        {
            var paper = FindPluginStartupPaper(descriptor.Id, descriptor.Manifest!.StartupPaper!.InstanceKey);
            return (Paper: paper, Visible: paper?.IsVisible, Collapsed: paper?.IsCollapsed,
                VisibilityVersion: paper == null ? 0 : _visibilityAnimationVersions.GetValueOrDefault(paper.Id));
        });
        bool StillOwnsStartupIntent(PaperBodyPluginDescriptor descriptor)
        {
            if (restoreGeneration != _paperSurfaceRestoreGeneration) return false;
            var request = requests[descriptor.Id];
            var paper = FindPluginStartupPaper(descriptor.Id, descriptor.Manifest!.StartupPaper!.InstanceKey);
            return ReferenceEquals(paper, request.Paper) && (paper == null ||
                (paper.IsVisible == request.Visible && paper.IsCollapsed == request.Collapsed &&
                 _visibilityAnimationVersions.GetValueOrDefault(paper.Id) == request.VisibilityVersion));
        }
        try
        {
            // Even''')
edit('src/AppController.PluginStartup.cs', '            EnsurePluginStartupPapers(candidates);',
'''            // Startup defaults lose to commands issued while shell preparation was pending.
            // Runtime initialization is independent and must still run for hidden entity papers.
            EnsurePluginStartupPapers(candidates, StillOwnsStartupIntent);''')
edit('src/AppController.PluginStartup.cs', '        IReadOnlyList<PaperBodyPluginDescriptor> descriptors)',
'''        IReadOnlyList<PaperBodyPluginDescriptor> descriptors,
        Func<PaperBodyPluginDescriptor, bool> stillOwnsStartupIntent)''')
edit('src/AppController.PluginStartup.cs', '''            if (!IsPluginEnabled(descriptor.Id))
            {
                continue;
            }

            var startup = descriptor.Manifest?.StartupPaper;
            if (startup == null''', '''            if (!IsPluginEnabled(descriptor.Id) || !stillOwnsStartupIntent(descriptor))
            {
                continue;
            }

            var startup = descriptor.Manifest?.StartupPaper;
            if (startup == null''')
p = Path('src/AppController.PluginStartup.cs')
s = p.read_text(encoding='utf-8-sig')
a = s.index('            var paper = State.Papers.FirstOrDefault(candidate =>', s.index('    private void EnsurePluginStartupPapers'))
b = s.index('            if (paper != null &&', a)
s = s[:a] + '            var paper = FindPluginStartupPaper(descriptor.Id, startup.InstanceKey);\n' + s[b:]
a = s.index('    private bool StartupSettingEnabled(')
s = s[:a] + '''    private PaperData? FindPluginStartupPaper(string providerId, string instanceKey) =>
        State.Papers.FirstOrDefault(paper =>
            string.Equals(paper.StartupOwnerPluginId, providerId, StringComparison.Ordinal) &&
            string.Equals(paper.StartupInstanceKey, instanceKey, StringComparison.Ordinal));

''' + s[a:]
p.write_text(s, encoding='utf-8', newline='\n')
edit('src/AppController.cs', '        await RestorePaperSurfacesAsync(papersToRestore);',
'''        var restore = RestorePaperSurfacesAsync(papersToRestore);
        var restoreGeneration = _paperSurfaceRestoreGeneration;
        await restore;''')
edit('src/AppController.cs', '''                startupDeferredPaperIds.ToArray(),
                _paperSurfaceRestoreGeneration);''', '''                startupDeferredPaperIds.ToArray(),
                restoreGeneration);''')
edit('src/AppController.cs', '''        RefreshMcpRuntime();
        _ = SchedulePluginStartupPapers(initialVisibilityCommand, _paperSurfaceRestoreGeneration);
''', '''        RefreshMcpRuntime();
        _ = SchedulePluginStartupPapers(initialVisibilityCommand, restoreGeneration);
''')
