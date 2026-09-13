from pathlib import Path
p=Path('.github/e004-app.ps1')
s=p.read_text(encoding='utf-8-sig')
def sub(a,b,n=1):
 global s
 if s.count(a)!=n:raise RuntimeError(f'expected {n} matches, saw {s.count(a)} for {a[:80]!r}')
 s=s.replace(a,b)
sub("@('multifile','compressed')", "@('fddsingle','scr2r')",3)
sub("$selfContained = if ($format -eq 'compressed') { 'true' } else { 'false' }", """$selfContained = if ($format -eq 'scr2r') { 'true' } else { 'false' }
      $singleFile = if ($format -eq 'fddsingle') { 'true' } else { 'false' }
      $readyToRun = if ($format -eq 'scr2r') { 'true' } else { 'false' }""")
sub('"/p:PublishSingleFile=$selfContained" "/p:EnableCompressionInSingleFile=$selfContained"', '"/p:PublishSingleFile=$singleFile" /p:EnableCompressionInSingleFile=false')
sub('/p:PublishReadyToRun=false', '"/p:PublishReadyToRun=$readyToRun"')
sub('  $rows | Format-Table -AutoSize | Out-String -Width 240 | Write-Host', '''  foreach ($profileFile in Get-ChildItem $fixtures -Recurse -Filter startup.prof) {
    Copy-Item $profileFile.FullName (Join-Path $evidence ($profileFile.Directory.Name + '.prof'))
  }
  $rows | Format-Table -AutoSize | Out-String -Width 240 | Write-Host''')
p.write_text(s,encoding='utf-8',newline='\n')
print('Reusing the exact real App probe, only adding FDD single-file and SC multi-file R2R formats.')
