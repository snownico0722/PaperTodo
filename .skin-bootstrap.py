from pathlib import Path
import re
import subprocess

MAIN = 'e074d84084921a2c3d16e7d0448e725706bf8779'
MICA = 'fcb46add0eef51f48da3b995e71c47eb249d0a98'

def source(ref, path):
    return subprocess.check_output(['git', 'show', f'{ref}:{path}']).decode('utf-8-sig')

def read(path):
    return Path(path).read_text(encoding='utf-8-sig')

def write(path, value):
    Path(path).write_text(value, encoding='utf-8')

conflicts = subprocess.check_output(['git','diff','--name-only','--diff-filter=U'], text=True).splitlines()
expected = {'.github/workflows/pull-request-build.yml', 'doc/DECISIONS.md', 'src/AppController.Settings.cs'}
assert set(conflicts) == expected, conflicts
block = re.compile(r'^<<<<<<<[^\n]*\n(.*?)^=======\n(.*?)^>>>>>>>[^\n]*\n?', re.M | re.S)
for path in ('.github/workflows/pull-request-build.yml', 'src/AppController.Settings.cs'):
    both = path.endswith('.yml')
    merged = block.sub(lambda m: m[1] + m[2] if both else m[2], read(path))
    assert '<<<<<<<' not in merged
    write(path, merged)

# main and Mica used the same new decision IDs independently. Preserve both histories.
main = source(MAIN, 'doc/DECISIONS.md')
mica = source(MICA, 'doc/DECISIONS.md')
appendix = mica[mica.index('## D-030 — 普通窗口原生 Mica'):]
rows = '\n'.join(line for line in mica.splitlines() if re.match(r'\| D-03[012] \|', line))
remap = {'D-030':'D-032','D-031':'D-033','D-032':'D-034'}
rename = lambda text: re.sub(r'D-03[012]', lambda m: remap[m[0]], text)
main_lines = main.splitlines()
last_row = max(i for i, line in enumerate(main_lines) if re.match(r'\| D-\d+ \|', line))
main_lines.insert(last_row + 1, rename(rows))
write('doc/DECISIONS.md', '\n'.join(main_lines).rstrip() + '\n\n---\n\n' + rename(appendix).rstrip() + '\n')
architecture = read('doc/ARCHITECTURE.md')
architecture = '\n'.join(rename(line) if ('Mica' in line or '云母' in line or '亚克力' in line) else line for line in architecture.splitlines()) + '\n'
write('doc/ARCHITECTURE.md', architecture)

# The main settings sidebar now owns root reconstruction; retain native backdrop refresh there.
path = 'src/AppController.SettingsSidebar.cs'
s = read(path)
needle = '        ApplySettingsSidebarFrame(window);\n'
assert s.count(needle) == 1
s = s.replace(needle, needle + '        _settingsMica?.Refresh(Theme.IsMica, Theme.IsDark, State.MicaBackdropType, State.MicaAlwaysActive, force: true);\n')
s = s.replace('CornerRadius = new CornerRadius(10),', 'CornerRadius = new CornerRadius(UsesNativeMicaWindows ? NativeMicaBackdrop.CornerRadius : 10),', 1)
write(path, s)

for path in conflicts:
    subprocess.run(['git','add',path], check=True)
subprocess.run(['git','add','src/AppController.SettingsSidebar.cs','doc/ARCHITECTURE.md'], check=True)
assert not subprocess.check_output(['git','diff','--name-only','--diff-filter=U'], text=True).strip()

# Temporary read-only source index; removed together with this bootstrap before delivery.
paths = ['src/PaperWindow.cs','src/PaperWindow.Capsule.cs','src/PaperWindow.VisualTree.cs',
         'src/EdgeCapsuleHost.cs','src/EdgeCapsuleDragWindow.cs','src/MasterCapsuleWindow.cs',
         'src/AppController.cs','src/AppController.Settings.cs','src/AppController.Settings.GeneralPage.cs',
         'src/Models.cs','src/StateStore.cs','src/Strings.cs']
patterns = re.compile(r'PaperSurfaceBrush|_paperChrome =|new Border|UpdateTheme|RefreshNativeMica|MicaBackdrop|MicaAlways|ColorScheme|BuildVisual|SetColorScheme|Clone|SettingsColor|UseCapsuleMode|CreatePaperChromeShadow')
index = []
for path in paths:
    if not Path(path).exists(): continue
    lines = read(path).splitlines()
    index.append(f'\n### {path} ({len(lines)} lines)')
    index += [f'{i}: {line}' for i, line in enumerate(lines, 1) if patterns.search(line)]
write('.skin-context.txt', '\n'.join(index) + '\n')
subprocess.run(['git','add','.skin-context.txt'], check=True)
