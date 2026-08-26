from pathlib import Path
import re


def run_script_prefix(path, stop_marker=None):
    text = Path(path).read_text(encoding='utf-8')
    if stop_marker is not None:
        if stop_marker not in text:
            raise SystemExit(f'{path}: stop marker not found')
        text = text.split(stop_marker, 1)[0]
    scope = {'__name__': '__main__', '__file__': path}
    exec(compile(text, path, 'exec'), scope, scope)


# Apply the already-reviewed product transformations. Phase 5's old policy-edit tail depended on
# phase 4 having committed first, so execute only its product portion; phase 4 then writes the final
# unified policy checks and docs in this same worktree.
run_script_prefix(
    'tools/unify_runtime_phase5.py',
    '# Policy check: the deleted host feature must stay deleted.')
run_script_prefix('tools/unify_runtime_phase4.py')

# The old architecture appendix duplicated the now-deleted Web per-Paper backend model. Remove it
# rather than leaving contradictory current architecture at the bottom of the file.
arch = Path('ARCHITECTURE.md')
text = arch.read_text(encoding='utf-8')
legacy_heading = '\n## Web Paper Runtime 生命周期\n'
if legacy_heading in text:
    text = text.split(legacy_heading, 1)[0].rstrip() + '\n'
arch.write_text(text, encoding='utf-8')

# Strengthen the policy check after phase 5 deleted the Body background-lifetime protocol.
policy = Path('tests/PaperTodo.ProtocolPolicyChecks/Program.cs')
text = policy.read_text(encoding='utf-8')
needle = '''        Assert(manifest.GetProperty("PaperRuntime") == null &&
               manifest.GetProperty("PaperRuntimePath") == null,
            "paperRuntime manifest fields must not return.");'''
addition = needle + '''
        Assert(manifest.GetProperty("Requires") == null,
            "Body requires/backgroundUpdates must not return as a host lifecycle mode.");
        Assert(abstractions.GetType("PaperTodo.Plugin.PaperBodyRuntimeRequirements", throwOnError: false) == null,
            "PaperBodyRuntimeRequirements must stay deleted; guaranteed background work belongs to Runtime.");'''
if needle not in text:
    raise SystemExit('finalize: unified policy insertion point not found')
text = text.replace(needle, addition, 1)
policy.write_text(text, encoding='utf-8')

# Clean a few remaining current-doc terms that would suggest two backend kinds. Historical D-024 is
# intentionally allowed to name the retired API while explaining why it must not return.
for path in [
    Path('ARCHITECTURE.md'),
    Path('plugin-samples/README.md'),
    Path('plugin-samples/PaperTodo.Plugin.OfficialClockWeb/README.md')
]:
    text = path.read_text(encoding='utf-8')
    text = text.replace('app runtime', 'Runtime')
    text = text.replace('App Runtime', 'Runtime')
    text = text.replace('app-runtime', 'Runtime')
    path.write_text(text, encoding='utf-8')

print('unified runtime final convergence complete')
