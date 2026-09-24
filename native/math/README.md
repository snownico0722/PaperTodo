# PaperTodo native math bridge

This directory builds the small native layout library used by PaperTodo's Markdown formula presentation.
The bridge accepts UTF-8 LaTeX math, delegates parsing and layout to RaTeX, and returns a bounded
JSON DisplayList plus logical dimensions and baseline. It does not rasterize formulas, own note
content, or create a second Markdown document.

RaTeX is pinned in Cargo.toml to commit c902516816cdc84519827d8b46d1cd40270d0451.
The matching unmodified KaTeX TTF source files are retained under assets/math-fonts/. The build
embeds both those fonts and papertodo_math.dll into the PaperTodo assembly, so the existing
single-file release packages do not require formula sidecars. At runtime PaperTodo extracts a
content-hashed temporary copy only for APIs that require a filesystem font/DLL path.

Before serializing a DisplayList, the bridge normalizes mathematical alphanumeric Unicode scalars
to the exact cmap slot RaTeX uses for those TTFs. Managed WPF code can therefore map
font + char_code + position + scale directly to GlyphRun without duplicating RaTeX's font-remapping
rules.

## Wire protocol

papertodo_math_render keeps the existing C ABI shape but now returns UTF-8 JSON rather than PNG
bytes. Protocol version 1 wraps the RaTeX display_list with a numeric version field.

DisplayList coordinates are RaTeX em units with y increasing downward. GlyphPath entries are
rendered by WPF GlyphRun; lines, rectangles, and arbitrary paths are rendered by WPF geometry.
The managed side validates protocol version, dimensions, item counts, path-command counts, font
availability, and glyph availability before publishing an immutable DrawingGroup.

## Build

On 64-bit Windows with the MSVC Rust toolchain installed:

    .\native\math\build.ps1 -Configuration Release -ForceRebuild

The output is written to native/math/bin/win-x64/papertodo_math.dll. When a checked-in precompiled
DLL is already present, running the script without -ForceRebuild keeps it, matching the repository's
native LMDB workflow.

## Failure behavior

The C ABI bounds source and output sizes and catches Rust panics. Managed callers must free every
successful result with papertodo_math_free. PaperTodo treats every non-zero status, missing DLL,
unsupported expression, malformed protocol payload, missing bundled font, or unresolved glyph as
a rendering miss and leaves the original Markdown source visible.
