from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / 'src/AppController.Settings.cs'
value = path.read_text(encoding='utf-8')
value = value.replace('            State.CapsuleCollapseAllActive = false;\n', '')
path.write_text(value, encoding='utf-8', newline='')
print('phase1 compile-gap fixes applied')
