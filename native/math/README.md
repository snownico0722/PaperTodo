# PaperTodo native math bridge

This directory builds the small native library used by PaperTodo's Markdown formula presentation.
The bridge accepts UTF-8 LaTeX math, delegates parsing/layout/rasterization to
[RaTeX](https://github.com/erweixin/RaTeX), and returns a transparent PNG plus logical dimensions
and baseline. It does not own note content or create a second Markdown document.

RaTeX is pinned in `Cargo.toml` to commit
`c902516816cdc84519827d8b46d1cd40270d0451`. The `embed-fonts` feature embeds the KaTeX math
fonts used by RaTeX, so a deployed PaperTodo directory only needs `papertodo_math.dll`.

## Build

On 64-bit Windows with the MSVC Rust toolchain installed:

```powershell
.\native\math\build.ps1 -Configuration Release -ForceRebuild
```

The output is written to `native/math/bin/win-x64/papertodo_math.dll`. When a checked-in
precompiled DLL is already present, running the script without `-ForceRebuild` keeps it, matching
the repository's native LMDB workflow.

## Failure behavior

The C ABI bounds source and output sizes and catches Rust panics. Managed callers must free every
successful result with `papertodo_math_free`. PaperTodo treats every non-zero status, missing DLL,
unsupported expression, or oversized result as a rendering miss and leaves the original Markdown
source visible.
