"""Temporary exact patch transport; removed from the final feature tree."""
import base64
import hashlib
import lzma
import pathlib
import subprocess

root = pathlib.Path(__file__).resolve().parent.parent

def git(*args):
    return subprocess.run(['git', *args], cwd=root, check=True)

payload = ''.join((root / '.github' / ('data-reload-patch.' + part)).read_text(encoding='utf-8').strip() for part in ('a', 'b'))
patch = lzma.decompress(base64.b64decode(payload, validate=True))
assert hashlib.sha256(patch).hexdigest() == '7b40b28d51f14349921a1bb7ccab7337179d0cc9856d4ff2a26a5b4dfad82ae1', 'Patch bytes changed'
git('config', 'core.autocrlf', 'false')
git('checkout-index', '--all', '--force')
patch_file = root / '.git' / 'data-reload.patch'
patch_file.write_bytes(patch)
forward = subprocess.run(['git', 'apply', '--check', str(patch_file)], cwd=root, capture_output=True)
if forward.returncode == 0:
    git('apply', str(patch_file))
else:
    reverse = subprocess.run(['git', 'apply', '--reverse', '--check', str(patch_file)], cwd=root, capture_output=True)
    if reverse.returncode != 0:
        raise RuntimeError(forward.stderr.decode('utf-8', errors='replace'))
git('add', '-N', '--', 'src', 'PaperTodo.Plugin.Abstractions', 'Resources', 'tests', 'plugin-samples/README.md', 'doc/ARCHITECTURE.md', 'doc/DECISIONS.md', 'doc/CHANGELOG.en.md', 'CHANGELOG.md', '.github/workflows/pull-request-build.yml')
git('diff', '--check')
print('Verified exact implementation patch:', len(patch), 'bytes', flush=True)
