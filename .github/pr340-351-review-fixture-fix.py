from pathlib import Path
p = Path('tests/PaperTodo.DataReloadChecks/DeferredExitChecks.cs')
s = p.read_text(encoding='utf-8')
assert s.count('controller?.IsExiting == true') == 1
assert s.count('                    Require(!controller.IsExiting, "exit tore down a pending conflict review");\n') == 1
s = s.replace('controller?.IsExiting == true', 'controller != null')
s = s.replace('                    Require(!controller.IsExiting, "exit tore down a pending conflict review");\n', '')
p.write_text(s, encoding='utf-8', newline='')
