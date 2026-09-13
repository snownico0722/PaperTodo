from pathlib import Path
import sys
root=Path(sys.argv[1])
p=root/'src/StartupCompilationProfile.cs'
s=p.read_text(encoding='utf-8-sig')
s=s.replace('using System.IO;', 'using System.Diagnostics.CodeAnalysis;\nusing System.IO;',1)
a='    internal static void Start(string directory)\n    {\n        try'
b='''    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty CoreLib location intentionally skips profiles for bundled runtimes.")]
    internal static void Start(string directory)
    {
        // .NET 10 ignores in-memory modules in startup profiles. A bundled runtime only
        // produced an empty profile in E-004; FDD single-file and directory builds benefit.
        if (typeof(object).Assembly.Location.Length == 0)
            return;

        try'''
assert s.count(a)==1
p.write_text(s.replace(a,b),encoding='utf-8',newline='\n')
print('Final profile scope: file-backed runtime only; no directories/profile for SC bundles.')
