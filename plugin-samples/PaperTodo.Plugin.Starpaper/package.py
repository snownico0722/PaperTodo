"""Build the independent, zero-dependency Starpaper distribution using Python 3.10+."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent.parent


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=REPO / 'dist')
    parser.add_argument('--sync', action='store_true', help='Update the repository deployable copy. Exit PaperTodo first.')
    parser.add_argument('--check', action='store_true', help='Check the committed deployable copy against source.')
    args = parser.parse_args()
    manifest = json.loads((ROOT / 'plugin.json').read_text(encoding='utf-8'))
    if manifest['id'] != 'com.papertodo.starpaper' or manifest['apiVersion'] != '2.1':
        raise ValueError('Unexpected plugin identity/protocol; review packaging before changing these.')
    files = [ROOT / name for name in ['plugin.json', 'README.md', 'NOTICE.txt']]
    files += sorted(path for path in (ROOT / 'web').rglob('*') if path.is_file())
    target = REPO / 'plugins' / manifest['id']
    if args.sync:
        # Replace only our known files, one atomic rename at a time. Never delete user state/cache.
        for source in files:
            destination = target / source.relative_to(ROOT)
            destination.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(dir=destination.parent, delete=False) as temporary:
                temporary.write(source.read_bytes())
                temp = Path(temporary.name)
            try:
                temp.replace(destination)
            finally:
                temp.unlink(missing_ok=True)
    if args.check:
        for source in files:
            destination = target / source.relative_to(ROOT)
            if not destination.is_file() or destination.read_bytes() != source.read_bytes():
                raise ValueError(f'Deployable copy differs: {destination}')
    args.output.mkdir(parents=True, exist_ok=True)
    output = args.output / f'Starpaper-{manifest["version"]}.zip'
    with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for source in files:
            info = zipfile.ZipInfo(f'{manifest["id"]}/{source.relative_to(ROOT).as_posix()}', date_time=(2026, 9, 6, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, source.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    output.with_suffix('.zip.sha256').write_text(f'{digest}  {output.name}\n', encoding='ascii')
    print(f'{output}\nSHA256 {digest}')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError) as error:
        print(f'Packaging failed: {error}', file=sys.stderr)
        sys.exit(1)
