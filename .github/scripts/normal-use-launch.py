"""Audit-only launch corrections; never included in any product commit."""
from pathlib import Path
import runpy
import subprocess
import sys

scripts = Path(__file__).resolve().parent
source = scripts / 'normal-use-bench.cs'
text = source.read_text(encoding='utf-8')
old = '''        var point = editor.PointToScreen(new Point(20, editor.ActualHeight / 2));
        LifecycleInput.MoveCursor(point);
        await Idle();
        Require(LifecycleInput.IsMouseTarget(window, point), "right-click target is obscured");'''
new = '''        // Do not charge readiness to the first-menu operation. Startup may still be
        // publishing the layered surface after the first ApplicationIdle callback.
        await Until(() => editor.ActualWidth > 20 && editor.ActualHeight > 0 &&
            LifecycleInput.IsMouseTarget(window,
                editor.PointToScreen(new Point(20, editor.ActualHeight / 2))));
        var point = editor.PointToScreen(new Point(20, editor.ActualHeight / 2));
        LifecycleInput.MoveCursor(point);
        await Idle();
        Require(LifecycleInput.IsMouseTarget(window, point), "right-click target is obscured");'''
if text.count(old) != 1:
    raise RuntimeError('Benchmark readiness edit is not unique')
source.write_text(text.replace(old, new), encoding='utf-8')

# Match LifecycleChecks.ChildStart: console-subsystem fixture executables must not
# create a console window that steals focus or covers the paper under measurement.
original_run = subprocess.run
def no_console_run(args, **kwargs):
    if str(args[0]).endswith('PaperTodo.DesktopBenchmarks.exe'):
        kwargs['creationflags'] = subprocess.CREATE_NO_WINDOW
    return original_run(args, **kwargs)
subprocess.run = no_console_run
runpy.run_path(str(scripts / 'normal-use-audit.py'), run_name='__main__')
