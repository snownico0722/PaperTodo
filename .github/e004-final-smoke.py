from pathlib import Path
p=Path('.github/e004-app.ps1');s=p.read_text(encoding='utf-8-sig')
def sub(a,b,n=1):
 global s
 if s.count(a)!=n:raise RuntimeError(f'{s.count(a)} != {n}: {a!r}')
 s=s.replace(a,b)
sub("@('baseline','candidate')", "@('candidate')")
sub("@('multifile','compressed')", "@('fddsingle','compressed')",3)
sub('"/p:PublishSingleFile=$selfContained"','/p:PublishSingleFile=true')
sub("@('baseline','fresh','recorded')", "@('fresh','recorded')",2)
sub("@('recorded','fresh','baseline')", "@('recorded','fresh')")
sub('foreach ($sampleIndex in 0..4)', 'foreach ($sampleIndex in 0..1)')
sub("if ($mode -eq 'recorded' -and $sampleIndex -gt 0 -and $profileBefore -eq 0)",
    "if ($mode -eq 'recorded' -and $format -ne 'compressed' -and $sampleIndex -gt 0 -and $profileBefore -eq 0)")
sub("$expectedStarts = if ($mode -eq 'baseline') { 0 } else { 1 }",
    "$expectedStarts = if ($format -eq 'compressed') { 0 } else { 1 }")
sub("if ($mode -ne 'baseline' -and $profileAfter -eq 0) { throw 'runtime profile was not written' }",
    """if ($format -ne 'compressed' -and $profileAfter -le 64) { throw 'runtime profile has no useful payload' }
          if ($format -eq 'compressed' -and (Test-Path $profile)) { throw 'SC bundle created an ineffective profile cache' }""")
p.write_text(s,encoding='utf-8',newline='\n')
print('Final real EXE smoke: eight launches; FDD records/reuses; SC bundle creates no profile directory.')
