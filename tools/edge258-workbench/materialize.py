"""Reconstruct reviewed source trees and upload Git objects only. Never update refs or merge PRs."""
from pathlib import Path
import base64, json, os, subprocess, tempfile, urllib.request
ROOT=Path.cwd()
OLD='37fcf9b97f4750be22ce96056b2dd667bd87f213'
MAIN='2a84601d732d2c6a169e525bd55f373191784532'
EXPECTED={'core':'aa9d872330c19757a8508c0cd5203c8509dbcaff','full':'6ff2b5429f0f2fd3596df33d7b9921c912af71cf'}
TEMP=Path(tempfile.mkdtemp(prefix='edge258-',dir=os.environ['RUNNER_TEMP']))
CORE=TEMP/'core'; FULL=TEMP/'full'
def git(*args,cwd=ROOT):
 return subprocess.check_output(['git',*args],cwd=cwd)
for path,ref in [(CORE,MAIN),(FULL,OLD)]:
 subprocess.run(['git','worktree','add','--detach',str(path),ref],cwd=ROOT,check=True)
for filename in ['prepare.py','fix.py','tests.py']:
 text=(ROOT/'tools/edge258-workbench'/filename).read_text()
 text=text.replace('/mnt/data/PaperTodo-work',str(ROOT)).replace('/mnt/data/edge258-core',str(CORE)).replace('/mnt/data/edge258-full',str(FULL))
 exec(compile(text,filename,'exec'),{})
known={line.split(' ',1)[0] for line in git('rev-list','--objects',OLD,MAIN).decode().splitlines()}
base_tree=git('rev-parse',OLD+'^{tree}').decode().strip()
def post(endpoint,data):
 req=urllib.request.Request('https://api.github.com/repos/snownico0722/PaperTodo/git/'+endpoint,
  data=json.dumps(data).encode(),method='POST',headers={
   'Authorization':'Bearer '+os.environ['GH_TOKEN'],'Accept':'application/vnd.github+json',
   'Content-Type':'application/json','X-GitHub-Api-Version':'2022-11-28'})
 with urllib.request.urlopen(req,timeout=60) as response:return json.load(response)
result={}
for name,path in [('core',CORE),('full',FULL)]:
 git('add','-A',cwd=path);git('diff','--cached','--check',cwd=path)
 tree=git('write-tree',cwd=path).decode().strip()
 assert tree==EXPECTED[name],(name,tree,EXPECTED[name])
 entries=[]
 for line in git('diff-tree','-r','--raw','--no-commit-id','--no-abbrev',OLD,tree,cwd=path).decode().splitlines():
  metadata,filename=line.split('\t',1)
  oldmode,mode,oldsha,sha,status=metadata[1:].split()
  assert filename.startswith(('src/','tests/','doc/')) or filename in ('AGENTS.md','CHANGELOG.md'),filename
  if status=='D':entries.append({'path':filename,'mode':oldmode,'type':'blob','sha':None});continue
  assert mode=='100644',(filename,mode)
  if sha not in known:
   data=git('cat-file','blob',sha,cwd=path)
   uploaded=post('blobs',{'encoding':'base64','content':base64.b64encode(data).decode()})
   assert uploaded['sha']==sha,(filename,uploaded)
   known.add(sha)
  entries.append({'path':filename,'mode':mode,'type':'blob','sha':sha})
 actual=post('trees',{'base_tree':base_tree,'tree':entries})['sha']
 assert actual==tree,(name,actual,tree)
 result[name]={'tree':tree,'changed_paths_from_original':len(entries)}
 print(name.upper()+'_TREE='+tree)
(ROOT/'edge258-trees.json').write_text(json.dumps(result,indent=2)+'\n')
print('No refs updated. No pull requests merged.')
