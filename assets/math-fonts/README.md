# PaperTodo math fonts

These TTF files are the unmodified KaTeX fonts vendored by RaTeX commit
c902516816cdc84519827d8b46d1cd40270d0451.

PaperTodo intentionally ships the exact same revision instead of resolving system-installed KaTeX
fonts. RaTeX produces the layout coordinates and PaperTodo renders ordinary glyphs with WPF
GlyphRun; keeping the TTF bytes fixed prevents cmap, advance-width, and baseline drift across
machines.

The fonts remain under the SIL Open Font License 1.1. Retained attribution and full license text:
- native/math/upstream/KaTeX-fonts-NOTICE.txt
- native/math/upstream/SIL-OFL-1.1.txt

System CJK / emoji faces are only fallback paths for Unicode glyphs not provided by the KaTeX font set.
