"""Temporary exact patch transport; removed from the final feature tree."""
import base64
import hashlib
import lzma
import pathlib
import subprocess
import re

root = pathlib.Path(__file__).resolve().parent.parent

def git(*args):
    return subprocess.run(['git', *args], cwd=root, check=True)

payload = ''.join((root / '.github' / ('data-reload-patch.' + part)).read_text(encoding='utf-8').strip() for part in ('a', 'b'))
patch = lzma.decompress(base64.b64decode(payload, validate=True))
assert hashlib.sha256(patch).hexdigest() == '7b40b28d51f14349921a1bb7ccab7337179d0cc9856d4ff2a26a5b4dfad82ae1', 'Patch bytes changed'
git('config', 'core.autocrlf', 'false')
# checkout ran with the Windows default first. Rewrite the whole disposable worktree
# from its index so unchanged CRLF files do not appear as edits after disabling conversion.
git('checkout-index', '--all', '--force')
for name in re.findall(r'^--- a/(.+)$', patch.decode('utf-8'), re.MULTILINE):
    source = subprocess.check_output(['git', 'show', 'HEAD:' + name], cwd=root)
    (root / name).write_bytes(source)
patch_file = root / '.git' / 'data-reload.patch'
patch_file.write_bytes(patch)
git('apply', '--check', str(patch_file))
git('apply', str(patch_file))

def replace(name, old, new):
    path = root / name
    text = path.read_text(encoding='utf-8')
    if new in text:
        print('Already staged:', name, repr(new[:60]), flush=True)
        return
    if old not in text:
        raise RuntimeError(f'Missing staging anchor: {name}: {old!r}')
    path.write_bytes(text.replace(old, new).encode('utf-8'))
    print('Staged:', name, repr(old[:60]), flush=True)

replace('src/StateReloadModels.cs', '        Copy(AppProperties, next, current);',
    '        // Keep the committed comparison snapshot immutable, including dictionaries/new papers.\n        next = StateStore.CopyForReload(next);\n        Copy(AppProperties, next, current);')
replace('src/StateJsonMerge.cs', 'other.Skip(i + 1).Select(result.IndexOf)',
    'other.Skip(i + 1).Select(item => result.IndexOf(item))')
path = root / 'src/StateStore.cs'
text = path.read_text(encoding='utf-8')
if 'AddIfExists(paths, FilePath);' not in text:
    text, count = re.subn(r'(?m)^(\s*)AddIfExists\(paths, BackupPath\);',
        r'\1AddIfExists(paths, FilePath); // pending external edits can reference images absent from memory\n\1AddIfExists(paths, BackupPath);', text)
    if count != 1:
        raise RuntimeError('Expected exactly one image-protection path anchor')
    path.write_bytes(text.encode('utf-8'))
print('Primary image protection verified', flush=True)
path = root / 'tests/PaperTodo.DataReloadChecks/Program.cs'
text = path.read_text(encoding='utf-8').replace('papertodo-image://', 'i:')
path.write_bytes(text.encode('utf-8'))
replace('tests/PaperTodo.DataReloadChecks/Program.cs', '        snapshot.Papers[0].Content = "![exit](i:456)";',
    '        var external = s.Read(); external["papers"]![0]!["content"] = "![pending](i:789)"; s.Write(external);\n        Require(s.Store.TryCollectProtectedImageIds(s.Memory, out ids) && ids.Contains("789"), "pending external primary image not protected");\n        snapshot.Papers[0].Content = "![exit](i:456)";')
replace('tests/PaperTodo.DataReloadChecks/Program.cs', '        StateReloadModels.Apply(current, next);',
    '        next.GlobalHotkeys["fixture"] = "Ctrl+F1";\n        next.Papers.Add(new() { Id = "new-identity", Type = PaperTypes.Note });\n        StateReloadModels.Apply(current, next);')
replace('tests/PaperTodo.DataReloadChecks/Program.cs', '"private-set relation lost");',
    '"private-set relation lost");\n        current.GlobalHotkeys["fixture"] = "Ctrl+F2"; current.Papers[^1].Title = "live-only";\n        Require(next.GlobalHotkeys["fixture"] == "Ctrl+F1" && next.Papers[^1].Title == "", "live objects mutated the committed comparison snapshot");')
replace('tests/PaperTodo.DataReloadChecks/LiveChecks.cs',
    '    private static T Field<T>(object instance, string name) =>\n        (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;',
    '    private static T Field<T>(object instance, string name)\n    {\n        var type = instance.GetType();\n        if (type.GetField(name, Private) is { } field) return (T)field.GetValue(instance)!;\n        if (type.GetProperty(name, Private) is { } property) return (T)property.GetValue(instance)!;\n        throw new MissingMemberException(type.FullName, name);\n    }')
git('add', '-N', '--', 'src', 'PaperTodo.Plugin.Abstractions', 'Resources', 'tests', 'plugin-samples/README.md', 'doc/ARCHITECTURE.md', 'doc/DECISIONS.md', 'doc/CHANGELOG.en.md', 'CHANGELOG.md', '.github/workflows/pull-request-build.yml')
git('diff', '--check')
print('Verified exact implementation patch:', len(patch), 'bytes', flush=True)
