from pathlib import Path
import runpy
import sys
root=Path(sys.argv[1]);mode=sys.argv[2]
ns=runpy.run_path(str(Path(__file__).with_name('e003-variants.py')))
replace=ns['replace']
if mode in ('preview','combined'):
 p='src/PaperWindow.cs'
 replace(p,'''        RefreshPaperTitle();

        Grid.SetColumn(titleArea, 0);''','''        RefreshPaperTitle(invalidatePreview: false);

        Grid.SetColumn(titleArea, 0);''')
 replace(p,'''    public void RefreshPaperTitle()
    {''','''    public void RefreshPaperTitle() => RefreshPaperTitle(invalidatePreview: true);

    private void RefreshPaperTitle(bool invalidatePreview)
    {''')
 replace(p,'''        RefreshCapsuleLabel();
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit()''','''        RefreshCapsuleLabel(invalidatePreview);
        RefreshPaperContextMenus();
    }

    private void BeginTitleEdit()''')
if mode == 'measure' and 'internal Task StartStartupWork()' in (root/'src/EdgeCapsulePreview.Preload.cs').read_text(encoding='utf-8-sig'):
 replace('tests/PaperTodo.LifecycleChecks/Program.cs',
   '                await Until(() => cache.ArtifactCount == count, "early preview readiness");',
   '                await cache.StartStartupWork();\n                Require(cache.ArtifactCount == count, "early preview readiness");')
print('Refined initial Shell invalidation:',mode)
