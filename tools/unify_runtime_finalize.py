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

# The old helper can survive phase5's conservative regex because surrounding whitespace changed in
# earlier runtime work. Delete it explicitly: Body has no host-level background-runtime requirement.
body_file = Path('src/PaperWindow.PluginBodies.cs')
text = body_file.read_text(encoding='utf-8')
text = re.sub(
    r'\n\s*private bool BodyRequires\(PaperBodyRuntimeRequirements requirement\) =>\n\s*_bodyDescriptor != null &&\n\s*\(_bodyDescriptor\.RuntimeRequirements & requirement\) == requirement;\n',
    '\n',
    text,
    count=1)
body_file.write_text(text, encoding='utf-8')

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

# Clean current documentation so it teaches one backend model only.
for path in [
    Path('ARCHITECTURE.md'),
    Path('plugin-samples/README.md'),
    Path('plugin-samples/PaperTodo.Plugin.OfficialClockWeb/README.md')
]:
    text = path.read_text(encoding='utf-8')
    text = text.replace('app runtime', 'Runtime')
    text = text.replace('App Runtime', 'Runtime')
    text = text.replace('app-runtime', 'Runtime')
    text = text.replace('不需要后台运行却声明 `backgroundUpdates`；',
                        '不需要后台运行却声明 `appRuntime`；')
    text = text.replace('只声明实际需要的 permissions / `backgroundUpdates` / `appRuntime`；',
                        '只声明实际需要的 permissions / `appRuntime`；')
    path.write_text(text, encoding='utf-8')

print('unified runtime final convergence complete')
