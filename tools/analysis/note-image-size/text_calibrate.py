"""Recompute text coefficients from Windows controls, excluding reserved note examples."""
from pathlib import Path
import json
import unicodedata as u
import numpy as np

def units(text,size=18,bold=False,code=False):
    counts=np.zeros(4)
    scale=(size/18)**1.6*(1.08 if bold else 1)
    for char in text:
        if char.isspace(): continue
        n=ord(char)
        kind=(1 if 0x3040<=n<=0x30ff or 0x31f0<=n<=0x31ff or 0xff66<=n<=0xff9f else
              2 if 0xac00<=n<=0xd7a3 or 0x1100<=n<=0x11ff or 0x3130<=n<=0x318f else
              0 if n>=0x2e80 else 3)
        counts[kind]+=scale*(.6 if u.category(char).startswith('P') else 1)
    return counts

CALIBRATION_TEXT='晨光照进窗边，今天写下几句简单的记录。The quiet morning leaves room for a useful idea.'
CONTROLS={
 'japanese':'今日は新しいアイデアを書き留めました。静かな朝、窓を開けて空を見上げます。少しずつ前に進んでいきましょう。',
 'korean':'오늘의 작은 생각을 기록합니다. 창문을 열고 맑은 하늘을 바라보며 새로운 계획을 세웁니다. 천천히 한 걸음씩 나아갑니다.',
 'english':'A short note records what changed, why it matters, and the next step. Keep the details clear and leave enough time to review the result.',
 **{k:CALIBRATION_TEXT for k in ('plain','quote','bold','inlinecode','list')}
}

rows={r['name']:r for r in json.loads(Path(__file__).with_name('text-controls.json').read_text())['records']}
for target in ('jpeg','png'):
    wide=760 if target=='jpeg' else 550
    bg=.012 if target=='jpeg' else .0015
    def actual(k):
        r=rows['text-'+k+'-calibration']
        return r[target+'Bytes']-3500-r['width']*r['height']*bg-units('校准',30,True)[0]*wide
    latin=actual('english')/(80*units(CONTROLS['english'])[3])
    jp=80*units(CONTROLS['japanese']);kr=80*units(CONTROLS['korean'])
    kana=(actual('japanese')-jp[0]*wide)/jp[1]
    hangul=(actual('korean')-kr[3]*latin)/kr[2]
    print(target,'bodyLatin',latin,'kana',kana,'hangul',hangul)
    for kind in ('plain','quote','bold','inlinecode','list'):
        f=units(CONTROLS[kind],16.2 if kind=='inlinecode' else 18)*80
        predicted=f@[wide,kana,hangul,(350 if target=='jpeg' else 275) if kind=='inlinecode' else latin]
        print(kind,'actual text bytes',round(actual(kind)),'base estimate',round(predicted),'ratio',round(actual(kind)/predicted,4),'plain ratio',round(actual(kind)/actual('plain'),4))

# Per-decoration increments are paired differences with the same body text.
for target in ('jpeg', 'png'):
    bg = .012 if target == 'jpeg' else .0015
    plain = rows['text-plain-calibration']
    for kind in ('quote', 'list'):
        r = rows['text-'+kind+'-calibration']
        delta = r[target+'Bytes'] - plain[target+'Bytes'] - bg*(r['width']*r['height']-plain['width']*plain['height'])
        print(target, kind, 'bytes per occurrence', delta/80)
