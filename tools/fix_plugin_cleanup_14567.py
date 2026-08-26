from pathlib import Path

path = Path("src/PaperBodyPluginRegistry.cs")
text = path.read_text(encoding="utf-8")
old = "        _lastChangedProviderIds.Clear();\n"
if text.count(old) != 1:
    raise SystemExit(f"expected one retired provider-id cleanup, found {text.count(old)}")
path.write_text(text.replace(old, "", 1), encoding="utf-8")
print("removed retired provider-id dispose cleanup")
