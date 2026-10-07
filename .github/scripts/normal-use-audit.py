"""Temporary, isolated review runner. Never updates main or a PR branch ref."""
import base64
import json
import os
from pathlib import Path
import random
import shutil
import subprocess
import sys
import urllib.request

PR = int(sys.argv[1])
HEADS = {324: 'f55c392210ed559c160f78e7e027c21f6c7a8ad5', 325: '4112df23eb3bafbea4e2ff9165659cae439a973b', 326: 'a1b296fba72ea575de268b36ff6cc84737d55a16'}
MAIN = '39292c739901b74a0d718c4d697ecdc1183bdcc0'
ROOT = Path(os.environ['GITHUB_WORKSPACE'])
OUT = Path(os.environ['RUNNER_TEMP']) / ('normal-use-' + str(PR))
OUT.mkdir(parents=True, exist_ok=True)
SCRIPTS = ROOT / '.github/scripts'


def run(args, cwd=ROOT, name=None, expect=None, env=None):
    print('RUN', ' '.join(map(str, args)), flush=True)
    result = subprocess.run(list(map(str, args)), cwd=cwd, text=True, encoding='utf-8', errors='replace',
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, timeout=900)
    if name:
        (OUT / (name + '.log')).write_text(result.stdout, encoding='utf-8')
    print(result.stdout if result.returncode else '\n'.join(result.stdout.splitlines()[-35:]), flush=True)
    if expect is not None:
        if result.returncode == 0 or expect not in result.stdout:
            raise RuntimeError('Expected specific pre-fix failure not observed: ' + expect)
    elif result.returncode:
        raise RuntimeError('Command failed: ' + str(args))
    return result.stdout


def checkout(name, sha):
    path = Path(os.environ['RUNNER_TEMP']) / ('normal-work-' + str(PR) + '-' + name)
    run(['git', 'worktree', 'add', '--detach', path, sha])
    run(['git', 'submodule', 'update', '--init', '--recursive'], cwd=path)
    return path


def replace(path, old, new):
    text = path.read_text(encoding='utf-8')
    if text.count(old) != 1:
        raise RuntimeError('Replacement does not match exactly once: ' + str(path))
    path.write_text(text.replace(old, new), encoding='utf-8', newline='\n')


candidate = checkout('candidate', HEADS[PR])
if PR != 326:
    patch = SCRIPTS / ('normal-use-' + str(PR) + '.patch')
    run(['git', 'apply', '--check', patch], cwd=candidate)
    run(['git', 'apply', patch], cwd=candidate)
if PR == 324:
    replace(candidate / 'CHANGELOG.md',
        '- Improved Show/Hide All responsiveness with large Todo lists by refreshing only rows with linked-paper buttons while preserving ordinary Todo editors and text selections.',
        '- Improved Show/Hide All responsiveness by refreshing linked-paper buttons in place while preserving all Todo editors, text selections and native text undo history.')
    replace(candidate / 'CHANGELOG.zh.md',
        '- 优化“全部显示／隐藏纸片”在待办较多时的响应，只刷新关联纸片按钮所在的行，并保留普通待办的编辑器与文字选区。',
        '- 优化“全部显示／隐藏纸片”的响应，原位刷新关联纸片按钮，不重建待办行；普通行与关联行都保留原有编辑器、文字选区和文本撤销记录。')
run(['git', 'diff', '--check'], cwd=candidate)
run(['dotnet', 'build', 'PaperTodo.csproj', '-c', 'Release'], cwd=candidate, name='candidate-build')
run(['pwsh', '-NoProfile', '-File', 'tools/testing/Run-Checks.ps1', '-Group', 'regression'],
    cwd=candidate, name='candidate-regression')
if PR == 324:
    run(['dotnet', 'run', '--project', 'tests/PaperTodo.PersistenceChecks/PaperTodo.PersistenceChecks.csproj', '-c', 'Release'],
        cwd=candidate, name='candidate-persistence')
env = os.environ.copy()
env['PAPER_MICA_CAPTURE'] = str(OUT / 'material-pixels/native')
env['PAPER_SKIN_CAPTURE'] = str(OUT / 'material-pixels/skins')
run(['dotnet', 'run', '--project', 'tests/PaperTodo.MicaChecks/PaperTodo.MicaChecks.csproj', '-c', 'Release'],
    cwd=candidate, name='candidate-material', env=env)

samples = []
if PR in (324, 325):
    before = checkout('before', HEADS[PR])
    if PR == 324:
        # The exact new behavioral assertion must fail on the pre-fix branch. Compile first;
        # neither a compiler failure nor a startup error is accepted as a reproduction.
        test = 'tests/PaperTodo.LifecycleChecks/TodoVisibilityChecks.cs'
        shutil.copyfile(candidate / test, before / test)
        run(['dotnet', 'build', 'tests/PaperTodo.LifecycleChecks/PaperTodo.LifecycleChecks.csproj', '-c', 'Release'],
            cwd=before, name='before-proof-build')
        run(['dotnet', 'run', '--no-build', '--project', 'tests/PaperTodo.LifecycleChecks/PaperTodo.LifecycleChecks.csproj',
             '-c', 'Release', '--', '--case', 'todo-visibility'], cwd=before,
            name='before-proof', expect='direct visibility refresh did not use exactly one reconcile')
        run(['git', 'restore', test], cwd=before)
    baseline = checkout('main', MAIN)
    variants = {'main': baseline, 'before': before, 'candidate': candidate}
    helper_source = run(['git', 'show', HEADS[325] + ':tests/PaperTodo.LifecycleChecks/TodoContextMenuChecks.cs'])
    helper = ('using System.Runtime.InteropServices;\nusing System.Windows;\nusing System.Windows.Input;\n'
              'using System.Windows.Interop;\nusing PaperTodo;\n' +
              helper_source[helper_source.index('internal static class LifecycleInput'):])
    outputs = {}
    for label, directory in variants.items():
        tool = directory / 'tools/NormalUseAudit'
        tool.mkdir(parents=True)
        (tool / 'Program.cs').write_text((SCRIPTS / 'normal-use-bench.cs').read_text(encoding='utf-8'), encoding='utf-8')
        (tool / 'LifecycleInput.cs').write_text(helper, encoding='utf-8')
        (tool / 'NormalUseAudit.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
  <UseWPF>true</UseWPF><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  <AssemblyName>PaperTodo.DesktopBenchmarks</AssemblyName><CETCompat>false</CETCompat></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../PaperTodo.csproj" /></ItemGroup>
</Project>''', encoding='utf-8')
        run(['dotnet', 'build', tool / 'NormalUseAudit.csproj', '-c', 'Release'], cwd=directory,
            name='benchmark-build-' + label)
        outputs[label] = tool / 'bin/Release/net10.0-windows10.0.17763.0'
    rng = random.Random(324325)
    for count in (1, 5, 10):
        for sample in range(3):
            labels = list(variants)
            rng.shuffle(labels)
            for label in labels:
                fixture = OUT / ('fixture-' + label + '-' + str(count) + '-' + str(sample))
                fixture.mkdir()
                source = outputs[label]
                for file in source.iterdir():
                    if file.is_file() and (file.suffix in ('.exe', '.dll', '.pdb') or file.name.endswith(('.deps.json', '.runtimeconfig.json'))):
                        shutil.copy2(file, fixture / file.name)
                for locale in ('en', 'ja', 'ko', 'runtimes'):
                    if (source / locale).is_dir():
                        shutil.copytree(source / locale, fixture / locale)
                (fixture / '.normal-use-fixture').write_text('owned isolated data')
                text = run([fixture / 'PaperTodo.DesktopBenchmarks.exe', count, PR], cwd=fixture,
                           name='measure-' + label + '-' + str(count) + '-' + str(sample))
                records = [line.removeprefix('NORMAL_AUDIT ') for line in text.splitlines() if line.startswith('NORMAL_AUDIT ')]
                if len(records) != 1:
                    raise RuntimeError('Missing normal-use measurements')
                row = json.loads(records[0])
                row.update(variant=label, sample=sample)
                samples.append(row)
                (OUT / 'measurements.json').write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding='utf-8')
                shutil.rmtree(fixture)

changed = run(['git', 'diff', '--name-only'], cwd=candidate).splitlines()
allowed = {'src/PaperWindow.Todo.cs', 'src/PaperWindow.TodoLinksAndSelection.cs',
           'tests/PaperTodo.LifecycleChecks/TodoVisibilityChecks.cs', 'tests/PaperTodo.LifecycleChecks/TodoHistoryChecks.cs',
           'CHANGELOG.md', 'CHANGELOG.zh.md'}
if not set(changed).issubset(allowed):
    raise RuntimeError('Unexpected product diff: ' + str(changed))
patch_text = run(['git', 'diff'], cwd=candidate)
(OUT / 'final.patch').write_text(patch_text, encoding='utf-8')
result = {'pr': PR, 'parent': HEADS[PR], 'changed_files': changed, 'samples': len(samples),
          'regression': 'passed', 'material': 'passed'}
if changed:
    # Create reviewed Git objects only. The assistant later uses the connector's expected-SHA
    # update_ref to apply them to the original PR, after inspecting this evidence.
    api_root = 'https://api.github.com/repos/' + os.environ['GITHUB_REPOSITORY']
    token = os.environ['GH_TOKEN']
    def post(path, value):
        request = urllib.request.Request(api_root + path, json.dumps(value).encode(), method='POST',
            headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                     'Content-Type': 'application/json', 'X-GitHub-Api-Version': '2022-11-28'})
        with urllib.request.urlopen(request, timeout=60) as response:
            return json.load(response)
    tree_entries = []
    for name in changed:
        blob = post('/git/blobs', {'content': base64.b64encode((candidate / name).read_bytes()).decode(), 'encoding': 'base64'})
        tree_entries.append({'path': name, 'mode': '100644', 'type': 'blob', 'sha': blob['sha']})
    base_tree = run(['git', 'rev-parse', 'HEAD^{tree}'], cwd=candidate).strip()
    tree = post('/git/trees', {'base_tree': base_tree, 'tree': tree_entries})
    title = '[ci] fix(todo): ' + ('显隐原位更新关联按钮并保留编辑状态' if PR == 324 else '未改变行顺序时跳过重排计算并补小列表回归')
    commit = post('/git/commits', {'message': title, 'tree': tree['sha'], 'parents': [HEADS[PR]]})
    result.update(candidate=commit['sha'], tree=tree['sha'])
else:
    result['candidate'] = HEADS[PR]
(OUT / 'result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print('NORMAL_REVIEW_RESULT ' + json.dumps(result, ensure_ascii=False), flush=True)
